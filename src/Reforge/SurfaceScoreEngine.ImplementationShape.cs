using Microsoft.CodeAnalysis;

namespace Reforge;

// Pass 6 — implementation shape, the counterweight to surface. Class size and the structural
// dispatcher/flags smells; the per-declaration contents census is SurfaceScoreEngine.Contents.cs.
// Every point here lands on the internal axis, never the surface one.
public sealed partial class SurfaceScoreEngine
{
    /// <summary>
    /// Scores the implementation hiding behind the surface: per-class size (services/repos/
    /// controllers) and the structural action-dispatcher / flags smells. Mode-parameter and flags
    /// penalties apply only to methods that observably mutate state; the structural dispatcher
    /// applies to reads too, and a read-shape consolidation whose arms share a base query does not
    /// fire it.
    /// </summary>
    private async Task ScoreImplementationComplexityAsync(List<ClassifiedType> classified, ScoreReport report,
        Solution solution, CancellationToken ct, HashSet<string> analyzedAssemblies, MethodBehaviourMap behaviour)
    {
        var countW = _config.Weight("methodCount");
        var complexityW = _config.Weight("methodComplexity");
        var largeW = _config.Weight("largeClass");
        var dispW = _config.Weight("actionDispatcher");
        var mmpW = _config.Weight("mutationModeParameter");
        var flagsW = _config.Weight("flagsControlFlow");
        if (largeW == 0 && dispW == 0 && mmpW == 0 && flagsW == 0) return;

        // For interface propagation: where does each classified type live, and what file.
        var groupByType = new Dictionary<string, (string Group, string File)>(StringComparer.Ordinal);
        foreach (var ct2 in classified)
            groupByType[ct2.Type.ToDisplayString()] = (ct2.Group, ct2.File);

        foreach (var c in classified)
        {
            if (c.Type.TypeKind is not (TypeKind.Class or TypeKind.Struct)) continue;
            // Pure data carriers (DTOs) have no implementation to score.
            if (c.Tags.Contains("dto") && LooksLikeDataCarrier(c.Type, analyzedAssemblies)) continue;
            // Generated code (EF migrations, *.g.cs/*.Designer.cs) is not developer-controlled
            // implementation complexity — counting its huge Up()/Down() methods would swamp the
            // internal axis with noise that's also stable across commits (useless to the gate).
            //
            // Per declaration, not once from the primary file: a partial type spans files, which one
            // is primary is Roslyn's declaration order, and filtering on it charged the generated
            // half or skipped the handwritten one depending on that order. Matches SectionMetrics.
            var handwritten = HandwrittenDeclarations(c.Type, ct);
            if (handwritten.Count == 0) continue;

            if (largeW != 0 && IsSizeTrackedClass(c))
            {
                int classLoc = handwritten.Sum(ImplementationComplexity.NonBlankLines);
                int pts = ImplementationComplexity.LargeClassPoints(classLoc) * largeW;
                if (pts != 0)
                {
                    // Name a handwritten declaration: the charge is for that half.
                    var (classFile, classLine) = LocateDeclaration(handwritten[0], c);
                    AddEntry(report, c.Group, "largeClass", pts, c.Type, classFile, classLine, $"{c.Type.Name} ({classLoc} LOC)");
                }
            }

            foreach (var member in c.Type.GetMembers())
            {
                if (member is not IMethodSymbol m) continue;
                if (m.MethodKind != MethodKind.Ordinary) continue;
                if (m.AssociatedSymbol is not null) continue;
                if (m.IsImplicitlyDeclared) continue;

                var syntax = GetMethodSyntax(m, ct);
                if (syntax is null) continue;

                // Locate on the declaration that scored. For a partial method the symbol's own
                // location is the defining part, which may be the generated half.
                var (file, line) = LocateDeclaration(syntax, c);

                // Dispatcher / flags are surface-level smells — only public-ish methods.
                bool surfaceMethod = m.DeclaredAccessibility
                    is Accessibility.Public or Accessibility.Internal or Accessibility.ProtectedOrInternal;
                if (!surfaceMethod) continue;
                if (IsAllowedDispatcherMethod(c.Type, m)) continue;
                if (HasAllowedShapeParam(m)) continue;

                // A read is exempt from the mode and flags rules: a selector steering a projection
                // is fine. It is not exempt from the structural rule: a read whose arms are
                // operations of their own is separate getters behind one door.
                bool mutation = ImplementationComplexity.IsMutation(m, syntax);
                if (solution.GetDocument(syntax.SyntaxTree) is not { } doc) continue;
                var model = await doc.GetSemanticModelAsync(ct);
                if (model is null) continue;
                var d = ImplementationComplexity.AnalyzeDispatcher(m, syntax, model);
                if (!mutation && !d.Fires) continue;
                var dispatchEnumParams = ImplementationComplexity.DispatchEnumParams(m);
                bool genericVerb = ImplementationComplexity.IsGenericVerb(m.Name);
                bool stateEngine = ImplementationComplexity.LooksLikeStateEngine(syntax);
                bool structuralFired = false;
                // [Flags] params are excluded throughout: flagsControlFlow owns that smell, and a
                // flags argument is a set of independent toggles, not a selector between operations.
                var modeParams = dispatchEnumParams.Where(p => !ImplementationComplexity.IsFlagsEnum(p.Type)).ToList();

                if (d.Fires && !stateEngine && dispW != 0)
                {
                    // actionDispatcher: operations folded behind one selector. Each arm past the
                    // first pays what the method it replaces would pay on the surface, and each
                    // arm written in place pays the flat charge its own method would, less the
                    // nesting it carries only for sitting in the branch. So inline and delegated
                    // arms cost the same, and collapsing never costs less than keeping the methods
                    // at any width. The surcharges price the fold itself.
                    bool typedSelector = modeParams.Count > 0;
                    bool appSvc = c.Tags.Contains("applicationService");
                    int surcharge = 20
                        + (typedSelector ? 8 : 0)   // an action/mode enum: the fold is in the signature
                        + (genericVerb ? 8 : 0)     // Apply/Handle/Process/…: the name hides which operation
                        + (appSvc ? 10 : 0);        // on an application service: the fold is the app's API
                    int pts = (surcharge + (d.ArmCount - 1) * SurfacePrice(c, m, behaviour)) * dispW
                        + d.InlineArms * countW - d.NestingRelief * complexityW;
                    var selector = typedSelector ? $" on {EnumParamNames(modeParams)}" : "";
                    var kind = mutation ? "dispatch" : "read dispatch";
                    var detail = $"{m.Name} ({d.ArmCount}-arm {kind}{selector}; each arm is an operation of its own)";
                    AddEntry(report, c.Group, "actionDispatcher", pts, m, file, line, detail);
                    // A structural dispatcher declared on an interface is a contractual smell, not
                    // just an implementation one — bill the interface method too.
                    PropagateDispatcherToInterfaces(report, c.Type, m, "actionDispatcher", pts, detail, groupByType);
                    structuralFired = true;
                }

                // mutationModeParameter: a mutation carrying an action/mode enum selector that
                // folds distinct operations behind one signature, even when the body is small and
                // doesn't delegate (so it isn't caught structurally above). State engines are exempt.
                if (!mutation) continue;
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

    /// <summary>
    /// What a method pays on the surface axis for being published, routed as
    /// <see cref="ScoreDurableSurface"/> routes it: the price of each operation a selector folds in.
    /// </summary>
    private int SurfacePrice(ClassifiedType c, IMethodSymbol m, MethodBehaviourMap behaviour)
    {
        if (!c.IsExported || m.DeclaredAccessibility != Accessibility.Public) return 0;
        if (c.Tags.Contains("repositoryInterface")) return _config.Weight("repositoryInterfaceMethod");
        if (c.Tags.Contains("repositoryImplementation")) return _config.Weight("repositoryImplementationMethod");
        if (c.Tags.Contains("controller")) return _config.Weight("controllerAction");
        if (c.Tags.Contains("backgroundJob")) return 0;
        return _config.Weight(behaviour.IsWrite(m) ? "exportedWriteMethod" : "exportedReadMethod")
            + (behaviour.IsUsedExternally(m) ? 0 : _config.Weight("exportedUnusedExternally"));
    }

    private static string EnumParamNames(IReadOnlyList<IParameterSymbol> ps)
        => string.Join("/", ps.Select(p => p.Type.Name).Distinct());

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

    private static bool IsSizeTrackedClass(ClassifiedType c)
        => c.Tags.Contains("applicationService")
        || c.Tags.Contains("repositoryImplementation")
        || c.Tags.Contains("controller")
        || c.Tags.Contains("backgroundJob");

    /// <summary>Declarations in files a developer wrote. Empty only when every declaration is generated.</summary>
    private static List<Microsoft.CodeAnalysis.CSharp.Syntax.TypeDeclarationSyntax> HandwrittenDeclarations(
        INamedTypeSymbol type, CancellationToken ct)
    {
        var declarations = new List<Microsoft.CodeAnalysis.CSharp.Syntax.TypeDeclarationSyntax>();
        foreach (var r in type.DeclaringSyntaxReferences)
        {
            if (GeneratedCode.IsGeneratedFile(r.SyntaxTree.FilePath)) continue;
            if (r.GetSyntax(ct) is Microsoft.CodeAnalysis.CSharp.Syntax.TypeDeclarationSyntax tds)
                declarations.Add(tds);
        }
        return declarations;
    }

    private (string File, int Line) LocateDeclaration(SyntaxNode declaration, ClassifiedType fallback)
    {
        var identifier = declaration switch
        {
            Microsoft.CodeAnalysis.CSharp.Syntax.TypeDeclarationSyntax tds => tds.Identifier.GetLocation(),
            Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax mds => mds.Identifier.GetLocation(),
            _ => declaration.GetLocation()
        };
        return LocateMember(identifier, fallback);
    }

    /// <summary>
    /// The first declaration in a handwritten file; null when the method is only generated. Resolves
    /// <c>PartialImplementationPart</c> first, as <c>SectionMetricsAnalyzer</c> does: for a partial
    /// method <c>GetMembers</c> hands back the defining part, whose declaration may be the generated
    /// half while the body a developer wrote lives in the other one.
    /// </summary>
    private static Microsoft.CodeAnalysis.CSharp.Syntax.BaseMethodDeclarationSyntax? GetMethodSyntax(IMethodSymbol m, CancellationToken ct)
    {
        foreach (var r in (m.PartialImplementationPart ?? m).DeclaringSyntaxReferences)
        {
            if (GeneratedCode.IsGeneratedFile(r.SyntaxTree.FilePath)) continue;
            if (r.GetSyntax(ct) is Microsoft.CodeAnalysis.CSharp.Syntax.BaseMethodDeclarationSyntax bm)
                return bm;
        }
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
