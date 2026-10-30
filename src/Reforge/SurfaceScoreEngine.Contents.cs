using Microsoft.CodeAnalysis;

namespace Reforge;

// Contents census — the cost of owning a section: every declaration that carries code, priced
// for its own complexity and charged a flat amount for existing. Lands on the implementation-shape
// axis. See ContentsPricing.
public sealed partial class SurfaceScoreEngine
{
    /// <summary>
    /// <c>methodComplexity</c>: each declaration's own cognitive complexity through
    /// <see cref="ContentsPricing.Curve"/>, times the weight. <c>methodCount</c>: the weight per
    /// declaration, one entry per type so the report names the type rather than every method in it.
    /// The call-path fold is not scored; it only names, in the detail, how much a method reaches
    /// through helpers that only it calls.
    /// <para>Private nested types are not in the classified corpus, which is surface-oriented, but
    /// their code is the section's all the same: each is priced under its nearest classified
    /// container, so moving a helper into one changes nothing here.</para>
    /// </summary>
    private void ScoreContents(List<ClassifiedType> classified, ScoreReport report, CallPathFold fold, CancellationToken ct)
    {
        var complexityW = _config.Weight("methodComplexity");
        var countW = _config.Weight("methodCount");

        foreach (var owner in classified)
        foreach (var type in WithPrivateNested(owner.Type))
        {
            var c = type.Equals(owner.Type, SymbolEqualityComparer.Default) ? owner : owner with { Type = type };
            var units = ContentsPricing.Units(c.Type, ct);
            if (units.Count == 0) continue;

            if (!report.ContentsBySection.TryGetValue(c.Group, out var section))
                report.ContentsBySection[c.Group] = section = new ContentsCensus();
            foreach (var u in units)
            {
                section.Add(u);
                report.Contents.Add(u);
            }

            if (complexityW != 0)
            {
                foreach (var u in units)
                {
                    int own = u.Cognitive.Score;
                    if (own == 0) continue;
                    int pts = (int)Math.Round(ContentsPricing.Curve(own, _config.ComplexityThreshold,
                        _config.ComplexityTaxPercent, _config.ComplexityTaxExponent) * complexityW, MidpointRounding.AwayFromZero);
                    var (file, line) = LocateMember(u.Location, c);
                    if (u.Cognitive.NestedDominates) line = u.Cognitive.NestedLine;
                    AddEntry(report, c.Group, "methodComplexity", pts, u.Symbol, file, line, ComplexityDetail(u, fold));
                }
            }

            if (countW != 0)
            {
                int declarations = units.Count + units.Sum(u => u.LocalFunctions);
                var (file, line) = LocateMember(units[0].Location, c);
                AddEntry(report, c.Group, "methodCount", declarations * countW, c.Type, file, line,
                    $"{c.Type.Name} ({declarations} declaration{(declarations == 1 ? "" : "s")})");
            }
        }
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

    private static string ComplexityDetail(ContentsUnit u, CallPathFold fold)
    {
        var name = u.Symbol is IMethodSymbol { MethodKind: MethodKind.Constructor or MethodKind.StaticConstructor }
            ? $"{u.Symbol.ContainingType.Name}.ctor"
            : u.Symbol.Name;
        int own = u.Cognitive.Score;
        if (u.Symbol is IMethodSymbol m)
        {
            var path = fold.For(m, own, 0);
            if (path.FoldedMethods > 0 && path.Score > own)
                return $"{name} (CC {own}; {path.Score} over its {path.FoldedMethods + 1}-method call path, most in {path.TopContributor})";
        }
        return u.Cognitive.NestedDominates
            ? $"{name} (CC {own}, {u.Cognitive.NestedScore} in a nested function)"
            : $"{name} (CC {own})";
    }
}
