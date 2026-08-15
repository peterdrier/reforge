using Microsoft.CodeAnalysis;

namespace Reforge;

// Pass 6 — implementation complexity, the counterweight to surface. Cognitive complexity,
// size, and the structural dispatcher/flags smells. Every point here lands on the internal
// axis, never the surface one.
public sealed partial class SurfaceScoreEngine
{
    private const int CognitiveThreshold = 15;

    /// <summary>
    /// Scores the implementation hiding behind the surface: per-method cognitive complexity
    /// and length, per-class size (services/repos/controllers), and the structural
    /// action-dispatcher / flags smells. Dispatcher and flags penalties apply only to methods
    /// that observably mutate state — a read-shape consolidation on a query is exempt by
    /// behavior, not by parameter naming. All points land on the internal-complexity axis.
    /// </summary>
    private void ScoreImplementationComplexity(List<ClassifiedType> classified, ScoreReport report, CancellationToken ct)
    {
        var longW = _config.Weight("longMethod");
        var largeW = _config.Weight("largeClass");
        var cogW = _config.Weight("cognitiveComplexity");
        var dispW = _config.Weight("actionDispatcher");
        var gadW = _config.Weight("genericActionDispatcher");
        var mmpW = _config.Weight("mutationModeParameter");
        var flagsW = _config.Weight("flagsControlFlow");
        if (longW == 0 && largeW == 0 && cogW == 0 && dispW == 0 && gadW == 0 && mmpW == 0 && flagsW == 0) return;

        // For interface propagation: where does each classified type live, and what file.
        var groupByType = new Dictionary<string, (string Group, string File)>(StringComparer.Ordinal);
        foreach (var ct2 in classified)
            groupByType[ct2.Type.ToDisplayString()] = (ct2.Group, ct2.File);

        foreach (var c in classified)
        {
            if (c.Type.TypeKind is not (TypeKind.Class or TypeKind.Struct)) continue;
            // Pure data carriers (DTOs) have no implementation to score.
            if (c.Tags.Contains("dto") && LooksLikeDataCarrier(c.Type)) continue;
            // Generated code (EF migrations, *.g.cs/*.Designer.cs) is not developer-controlled
            // implementation complexity — counting its huge Up()/Down() methods would swamp the
            // internal axis with noise that's also stable across commits (useless to the gate).
            if (IsGeneratedFile(c.File)) continue;

            if (largeW != 0 && IsSizeTrackedClass(c))
            {
                int classLoc = ClassNonBlankLines(c.Type, ct);
                int pts = ImplementationComplexity.LargeClassPoints(classLoc) * largeW;
                if (pts != 0)
                    AddEntry(report, c.Group, "largeClass", pts, c.Type, c.File, c.Line, $"{c.Type.Name} ({classLoc} LOC)");
            }

            foreach (var member in c.Type.GetMembers())
            {
                if (member is not IMethodSymbol m) continue;
                if (m.MethodKind != MethodKind.Ordinary) continue;
                if (m.AssociatedSymbol is not null) continue;
                if (m.IsImplicitlyDeclared) continue;

                var syntax = GetMethodSyntax(m, ct);
                if (syntax is null) continue;

                var loc = m.Locations.FirstOrDefault(l => l.IsInSource);
                var (file, line) = LocateMember(loc, c);

                // Size + cognitive complexity apply to every method (private god methods are
                // exactly how complexity hides behind a shrunken public surface).
                if (longW != 0)
                {
                    int methodLoc = ImplementationComplexity.NonBlankLines(syntax);
                    int pts = ImplementationComplexity.LongMethodPoints(methodLoc) * longW;
                    if (pts != 0)
                        AddEntry(report, c.Group, "longMethod", pts, m, file, line, $"{m.Name} ({methodLoc} LOC)");
                }

                if (cogW != 0)
                {
                    int cc = ImplementationComplexity.Cognitive(syntax);
                    int over = cc - CognitiveThreshold;
                    if (over > 0)
                        AddEntry(report, c.Group, "cognitiveComplexity", over * cogW, m, file, line, $"{m.Name} (CC {cc})");
                }

                // Dispatcher / flags are surface-level smells — only public-ish methods, and
                // only when the method mutates. Read methods are exempt by behavior.
                bool surfaceMethod = m.DeclaredAccessibility
                    is Accessibility.Public or Accessibility.Internal or Accessibility.ProtectedOrInternal;
                if (!surfaceMethod) continue;
                if (IsAllowedDispatcherMethod(c.Type, m)) continue;
                if (HasAllowedShapeParam(m)) continue;
                // Read methods (including *ReadShape / *Query shaped reads) are exempt by behavior.
                if (!ImplementationComplexity.IsMutation(m, syntax)) continue;

                var d = ImplementationComplexity.AnalyzeDispatcher(m, syntax);
                var dispatchEnumParams = ImplementationComplexity.DispatchEnumParams(m);
                bool genericVerb = ImplementationComplexity.IsGenericVerb(m.Name);
                bool stateEngine = ImplementationComplexity.LooksLikeStateEngine(syntax);
                bool structuralFired = false;

                if (d.Fires && !stateEngine)
                {
                    if (genericVerb && dispatchEnumParams.Count > 0 && gadW != 0)
                    {
                        // genericActionDispatcher: named generic verb + action/mode enum + a body
                        // that switches and delegates arms to distinct members. The strongest,
                        // most-attributable dispatcher smell — also flagged on the interface method.
                        bool appSvc = c.Tags.Contains("applicationService") || IsApplicationServiceType(c.Type);
                        int basePts = 20 + 8 * Math.Max(0, d.ArmCount - 2) + (appSvc ? 10 : 0) + 10 /* mutation */;
                        int pts = basePts * gadW;
                        var detail = $"{m.Name} ({d.ArmCount}-arm generic dispatch on {EnumParamNames(dispatchEnumParams)}; arms route to distinct members)";
                        AddEntry(report, c.Group, "genericActionDispatcher", pts, m, file, line, detail);
                        PropagateDispatcherToInterfaces(report, c.Type, m, "genericActionDispatcher", pts, detail, groupByType);
                        structuralFired = true;
                    }
                    else if (dispW != 0)
                    {
                        int basePts = 20 + 5 * (d.ArmCount - 2);
                        AddEntry(report, c.Group, "actionDispatcher", basePts * dispW, m, file, line,
                            $"{m.Name} ({d.ArmCount}-arm dispatch; arms route to distinct members)");
                        structuralFired = true;
                    }
                }

                // mutationModeParameter: a mutation carrying an action/mode enum selector that
                // folds distinct operations behind one signature, even when the body is small and
                // doesn't delegate (so it isn't caught structurally above). [Flags] params are
                // excluded — flagsControlFlow already owns that smell. State engines are exempt.
                var modeParams = dispatchEnumParams.Where(p => !ImplementationComplexity.IsFlagsEnum(p.Type)).ToList();
                if (!structuralFired && !stateEngine && modeParams.Count > 0 && mmpW != 0)
                {
                    int basePts = 10 + 5 * modeParams.Count + (genericVerb ? 10 : 0);
                    int pts = basePts * mmpW;
                    var detail = $"{m.Name} ({EnumParamNames(modeParams)} mode/action param)";
                    AddEntry(report, c.Group, "mutationModeParameter", pts, m, file, line, detail);
                    PropagateDispatcherToInterfaces(report, c.Type, m, "mutationModeParameter", pts, detail, groupByType);
                }

                if (flagsW != 0)
                {
                    int flagTests = ImplementationComplexity.FlagsTestCount(m, syntax);
                    if (flagTests > 0)
                    {
                        int basePts = 8 + 4 * Math.Max(0, flagTests - 2);
                        AddEntry(report, c.Group, "flagsControlFlow", basePts * flagsW, m, file, line,
                            $"{m.Name} ({flagTests} flag tests)");
                    }
                }
            }
        }
    }

    private static string EnumParamNames(IReadOnlyList<IParameterSymbol> ps)
        => string.Join("/", ps.Select(p => p.Type.Name).Distinct());

    private static bool IsApplicationServiceType(INamedTypeSymbol type)
        => type.Name == "IApplicationService"
        || type.AllInterfaces.Any(i => i.Name == "IApplicationService");

    /// <summary>
    /// When a dispatcher/mode smell fires on an implementation method, also attribute it to the
    /// interface method(s) it implements — the durable public surface is the interface, and an
    /// agent reading the report needs to see that <c>IShiftSignupService.ApplySignupActionAsync</c>
    /// is the generic dispatcher, not just the concrete class.
    /// </summary>
    private void PropagateDispatcherToInterfaces(ScoreReport report, INamedTypeSymbol implType,
        IMethodSymbol implMethod, string rule, int points, string detail,
        Dictionary<string, (string Group, string File)> groupByType)
    {
        foreach (var iface in implType.AllInterfaces)
        {
            if (!groupByType.TryGetValue(iface.ToDisplayString(), out var info)) continue;
            foreach (var im in iface.GetMembers().OfType<IMethodSymbol>())
            {
                if (im.Name != implMethod.Name) continue;
                var impl = implType.FindImplementationForInterfaceMember(im);
                if (!SymbolEqualityComparer.Default.Equals(impl, implMethod)) continue;

                var iloc = im.Locations.FirstOrDefault(l => l.IsInSource);
                string file; int line;
                if (iloc is not null)
                {
                    var ls = iloc.GetLineSpan();
                    file = LocationHelper.NormalizePath(ls.Path, _solutionDirectory);
                    line = ls.StartLinePosition.Line + 1;
                }
                else { file = info.File; line = 0; }
                AddEntry(report, info.Group, rule, points, im, file, line, detail);
            }
        }
    }

    private static bool IsGeneratedFile(string file)
    {
        if (string.IsNullOrEmpty(file)) return false;
        var f = file.Replace('\\', '/');
        return f.Contains("/Migrations/", StringComparison.OrdinalIgnoreCase)
            || f.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)
            || f.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase)
            || f.EndsWith(".generated.cs", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSizeTrackedClass(ClassifiedType c)
        => c.Tags.Contains("applicationService")
        || c.Tags.Contains("repositoryImplementation")
        || c.Tags.Contains("controller")
        || c.Tags.Contains("backgroundJob");

    private int ClassNonBlankLines(INamedTypeSymbol type, CancellationToken ct)
    {
        int total = 0;
        foreach (var r in type.DeclaringSyntaxReferences)
        {
            if (r.GetSyntax(ct) is Microsoft.CodeAnalysis.CSharp.Syntax.TypeDeclarationSyntax tds)
                total += ImplementationComplexity.NonBlankLines(tds);
        }
        return total;
    }

    private static Microsoft.CodeAnalysis.CSharp.Syntax.BaseMethodDeclarationSyntax? GetMethodSyntax(IMethodSymbol m, CancellationToken ct)
    {
        foreach (var r in m.DeclaringSyntaxReferences)
            if (r.GetSyntax(ct) is Microsoft.CodeAnalysis.CSharp.Syntax.BaseMethodDeclarationSyntax bm)
                return bm;
        return null;
    }

    private bool IsAllowedDispatcherMethod(INamedTypeSymbol type, IMethodSymbol m)
    {
        if (_config.AllowedDispatcherMethods.Count == 0) return false;
        var qualified = $"{type.Name}.{m.Name}";
        foreach (var pat in _config.AllowedDispatcherMethods)
            if (GlobMatcher.MatchesName(qualified, pat) || GlobMatcher.MatchesName(m.Name, pat))
                return true;
        return false;
    }

    private bool HasAllowedShapeParam(IMethodSymbol m)
    {
        if (_config.AllowedShapeTypes.Count == 0) return false;
        foreach (var p in m.Parameters)
            foreach (var pat in _config.AllowedShapeTypes)
                if (GlobMatcher.MatchesName(p.Type.Name, pat))
                    return true;
        return false;
    }
}
