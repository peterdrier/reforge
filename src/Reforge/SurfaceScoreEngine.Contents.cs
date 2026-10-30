using Microsoft.CodeAnalysis;

namespace Reforge;

// Contents census — the cost of owning a section: every declaration that carries code, priced
// for its own complexity and charged a flat amount for existing. Lands on the implementation-shape
// axis. See ContentsPricing.
public sealed partial class SurfaceScoreEngine
{
    /// <summary>
    /// <c>methodComplexity</c>: each declaration's own cognitive complexity through
    /// <see cref="ContentsPricing.Curve"/>, times the weight, times the depth and sharing factors.
    /// <c>methodCount</c>: the weight per declaration with fewer than two callers, one entry per type
    /// so the report names the type rather than every method in it. The call-path fold is not scored; it only names, in the
    /// detail, how much a method reaches through helpers that only it calls.
    /// <para>Private nested types are not in the classified corpus, which is surface-oriented, but
    /// their code is the section's all the same: each is priced under its nearest classified
    /// container, so moving a helper into one changes nothing here.</para>
    /// </summary>
    private async Task ScoreContentsAsync(Solution solution, List<ClassifiedType> classified, ScoreReport report,
        HashSet<string> analyzedAssemblies, CallPathFold fold, CancellationToken ct)
    {
        var complexityW = _config.Weight("methodComplexity");
        var countW = _config.Weight("methodCount");
        var internalW = _config.Weight("internalWiderThanUse");
        var protectedW = _config.Weight("protectedWiderThanUse");

        var contents = classified
            .SelectMany(owner => WithPrivateNested(owner.Type).Select(t =>
                t.Equals(owner.Type, SymbolEqualityComparer.Default) ? owner : owner with { Type = t }))
            .Select(c => (Type: c, Units: ContentsPricing.Units(c.Type, ct)))
            .Where(x => x.Units.Count > 0)
            .ToList();
        var graph = await ContentsGraph.BuildAsync(solution, contents.SelectMany(x => x.Units), analyzedAssemblies, ct);

        foreach (var (c, units) in contents)
        {
            if (!report.ContentsBySection.TryGetValue(c.Group, out var section))
                report.ContentsBySection[c.Group] = section = new ContentsCensus();

            foreach (var u in units)
            {
                var reach = new ContentsReach(graph.Depth(u.Symbol), graph.Callers(u.Symbol), graph.Reach(u.Symbol));
                section.Add(u, reach);
                report.Contents.Add(u, reach);

                if (WiderThanUse(u.Symbol, graph) is { } wider)
                {
                    var (vFile, vLine) = LocateMember(u.Location, c);
                    AddEntry(report, c.Group, wider == Accessibility.Internal ? "internalWiderThanUse" : "protectedWiderThanUse",
                        wider == Accessibility.Internal ? internalW : protectedW, u.Symbol, vFile, vLine,
                        $"{u.Symbol.Name}: {(wider == Accessibility.Internal ? "internal" : "protected")}, but nothing outside {u.Symbol.ContainingType.Name} names it");
                }

                int own = u.Cognitive.Score;
                if (complexityW == 0 || own == 0) continue;
                double factor = (1 + _config.ComplexityDepthPercent / 100.0 * reach.Depth)
                    * (1 + _config.ComplexitySharingPercent / 100.0 * Math.Log(Math.Max(1, reach.Reach)));
                int pts = (int)Math.Round(ContentsPricing.Curve(own, _config.ComplexityThreshold,
                    _config.ComplexityTaxPercent, _config.ComplexityTaxExponent) * complexityW * factor, MidpointRounding.AwayFromZero);
                var (file, line) = LocateMember(u.Location, c);
                if (u.Cognitive.NestedDominates) line = u.Cognitive.NestedLine;
                AddEntry(report, c.Group, "methodComplexity", pts, u.Symbol, file, line, ComplexityDetail(u, reach, fold));
            }

            if (countW != 0)
            {
                // A declaration with two or more callers is reused: merging it away means a copy per
                // caller, so the flat charge that exists to merge tiny pieces does not apply to it.
                int reused = units.Count(u => graph.Callers(u.Symbol) >= 2);
                int declarations = units.Count - reused + units.Sum(u => u.LocalFunctions);
                var (file, line) = LocateMember(units[0].Location, c);
                var detail = $"{c.Type.Name} ({declarations} declaration{(declarations == 1 ? "" : "s")}"
                    + (reused > 0 ? $"; {reused} reused, not charged)" : ")");
                AddEntry(report, c.Group, "methodCount", declarations * countW, c.Type, file, line, detail);
            }
        }
    }

    /// <summary>
    /// The visibility a method has beyond private when nothing needs it: <see cref="Accessibility.Internal"/>
    /// for a member effectively visible to its assembly (internal, or public on a type the assembly
    /// does not export), <see cref="Accessibility.Protected"/> for one visible to derived types, and
    /// null when it is private, priced as public surface, or used from outside its type. Widening a
    /// helper is never free: public pays as surface, and these pay less, in that order. Members that
    /// are overridden, override, implement an interface or carry an attribute are left alone, since
    /// their visibility is set by something other than their callers.
    /// </summary>
    private static Accessibility? WiderThanUse(ISymbol symbol, ContentsGraph graph)
    {
        if (symbol is not IMethodSymbol { MethodKind: MethodKind.Ordinary } m) return null;
        if (m.IsOverride || m.IsExtern || m.GetAttributes().Length > 0 || graph.IsOverridden(m)) return null;
        if (m.ExplicitInterfaceImplementations.Length > 0) return null;
        for (var t = m.ContainingType; t is not null; t = t.ContainingType)
            if (t.DeclaredAccessibility == Accessibility.Private) return null;
        var level = m.DeclaredAccessibility switch
        {
            Accessibility.Public when !SurfaceVisibility.IsExported(m.ContainingType) => Accessibility.Internal,
            Accessibility.Internal or Accessibility.ProtectedOrInternal => Accessibility.Internal,
            Accessibility.Protected or Accessibility.ProtectedAndInternal => Accessibility.Protected,
            _ => (Accessibility?)null
        };
        if (level is null || graph.UsedOutsideType(m)) return null;
        if (m.ContainingType.AllInterfaces.SelectMany(i => i.GetMembers())
            .Any(im => SymbolEqualityComparer.Default.Equals(m.ContainingType.FindImplementationForInterfaceMember(im), m)))
            return null;
        return level;
    }

    /// <summary>
    /// <paramref name="type"/> and the types nested in it that are effectively private, to any
    /// depth. A non-private nested type is classified on its own and walked from there.
    /// </summary>
    private static IEnumerable<INamedTypeSymbol> WithPrivateNested(INamedTypeSymbol type)
    {
        yield return type;
        foreach (var nested in type.GetTypeMembers())
        {
            if (nested.IsImplicitlyDeclared || !nested.Locations.Any(l => l.IsInSource)) continue;
            if (nested.DeclaredAccessibility != Accessibility.Private) continue;
            foreach (var t in WithNested(nested)) yield return t;
        }

        static IEnumerable<INamedTypeSymbol> WithNested(INamedTypeSymbol t)
        {
            yield return t;
            foreach (var n in t.GetTypeMembers())
                if (!n.IsImplicitlyDeclared && n.Locations.Any(l => l.IsInSource))
                    foreach (var d in WithNested(n)) yield return d;
        }
    }

    private string ComplexityDetail(ContentsUnit u, ContentsReach reach, CallPathFold fold)
    {
        var name = u.Symbol is IMethodSymbol { MethodKind: MethodKind.Constructor or MethodKind.StaticConstructor }
            ? $"{u.Symbol.ContainingType.Name}.ctor"
            : u.Symbol.Name;
        int own = u.Cognitive.Score;
        var parts = new List<string> { $"CC {own}" };
        if (u.Symbol is IMethodSymbol m && fold.For(m, own, 0) is { FoldedMethods: > 0 } path && path.Score > own)
            parts.Add($"{path.Score} over its {path.FoldedMethods + 1}-method call path, most in {path.TopContributor}");
        else if (u.Cognitive.NestedDominates)
            parts.Add($"{u.Cognitive.NestedScore} in a nested function");
        if (_config.ComplexityDepthPercent != 0 && reach.Depth > 0) parts.Add($"depth {reach.Depth}");
        if (_config.ComplexitySharingPercent != 0 && reach.Reach > 1) parts.Add($"reached from {reach.Reach}");
        return $"{name} ({string.Join("; ", parts)})";
    }
}
