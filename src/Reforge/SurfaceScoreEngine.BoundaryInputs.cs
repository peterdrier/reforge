using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Reforge;

// Pass 7 — boundary-input surface. A parameter object that absorbs a long argument list still
// carries the surface; these rules charge for it so the trade stays visible.
public sealed partial class SurfaceScoreEngine
{
    private sealed class BoundaryUsage
    {
        public BoundaryUsage(INamedTypeSymbol type) => Type = type;
        public INamedTypeSymbol Type { get; }
        public int MethodCount { get; set; }
        public HashSet<string> Families { get; } = new(StringComparer.Ordinal);
        public int MinBusinessParams { get; set; } = int.MaxValue;
    }

    /// <summary>
    /// Charges for parameter/command/request objects on the public boundary. A refactor that
    /// replaces <c>CreateCampAsync(a, b, c, d, e, f)</c> with
    /// <c>CreateCampAsync(CampRegistrationInput input)</c> drops methodParameterOverflow but the
    /// object still carries the same durable surface — and hides it if its accessors are
    /// internal. These rules (surface axis) restore that charge so the trade is visible.
    /// </summary>
    private void ScoreBoundaryInputs(List<ClassifiedType> classified,
        Dictionary<string, ClassifiedType> typesByDisplay, ScoreReport report, CancellationToken ct)
    {
        var hiddenW = _config.Weight("publicInputWithHiddenState");
        var bagW = _config.Weight("parameterBagInput");
        if (hiddenW == 0 && bagW == 0) return;

        // How is each boundary type used as a parameter of an exported method?
        var usage = new Dictionary<string, BoundaryUsage>(StringComparer.Ordinal);
        foreach (var c in classified)
        {
            // Only an exported type has a boundary for an input object to sit on.
            if (!c.IsExported) continue;
            foreach (var m in c.Type.GetMembers().OfType<IMethodSymbol>())
            {
                if (m.MethodKind != MethodKind.Ordinary) continue;
                if (m.AssociatedSymbol is not null || m.IsImplicitlyDeclared) continue;
                // Per-method, not per-type: an interface's members are implicitly public, but C# 8
                // allows private/internal ones, and an input object reachable only through those is
                // not on any boundary an external consumer can call.
                if (!SurfaceVisibility.IsExported(m)) continue;

                int businessParams = m.Parameters.Count(p => p.Type.Name != "CancellationToken");
                foreach (var p in m.Parameters)
                {
                    if (p.Type is not INamedTypeSymbol pt) continue;
                    if (!pt.Locations.Any(l => l.IsInSource)) continue;
                    if (!BoundaryInput.IsBoundaryName(pt.Name)) continue;

                    var key = pt.ToDisplayString();
                    if (!usage.TryGetValue(key, out var u))
                    {
                        u = new BoundaryUsage(pt);
                        usage[key] = u;
                    }
                    u.MethodCount++;
                    u.Families.Add(StripAsyncName(m.Name));
                    u.MinBusinessParams = Math.Min(u.MinBusinessParams, businessParams);
                }
            }
        }

        foreach (var u in usage.Values)
        {
            if (!typesByDisplay.TryGetValue(SolutionClassifier.TypeKey(u.Type), out var c)) continue;
            if (!c.IsExported) continue; // an internal input object is not boundary surface
            var t = u.Type;

            int dataMembers = BoundaryInput.DataMemberCount(t);
            int publicReadable = BoundaryInput.PublicReadableCount(t);
            int hidden = Math.Max(0, dataMembers - publicReadable);

            if (hiddenW != 0 && dataMembers >= 2 && publicReadable * 2 < dataMembers)
            {
                int basePts = 15 + 2 * Math.Max(0, hidden - 2);
                AddEntry(report, c.Group, "publicInputWithHiddenState", basePts * hiddenW, t, c.File, c.Line,
                    $"{t.Name} ({publicReadable}/{dataMembers} members publicly readable)");
            }

            int ctorParams = BoundaryInput.WidestCtorParamCount(t);
            int members = Math.Max(ctorParams, dataMembers);
            if (bagW != 0 && (ctorParams >= 4 || dataMembers >= 4)
                && !BoundaryInput.HasBehavior(t) && BoundaryInput.CtorIsDirectAssignment(t, ct)
                && u.MinBusinessParams <= 2)
            {
                int basePts = 12 + 2 * Math.Max(0, members - 4) + (u.Families.Count == 1 ? 8 : 0);
                AddEntry(report, c.Group, "parameterBagInput", basePts * bagW, t, c.File, c.Line,
                    $"{t.Name} ({members} members, no behavior; used by {string.Join("/", u.Families)})");
            }
        }
    }

    /// <summary>
    /// Counts call sites that construct a boundary input object inline inside a method call
    /// (<c>Foo(new XInput(a, b, c, d))</c>) — the complexity moved from the signature to the
    /// construction site rather than disappearing. +5 per site, capped at +25 per type.
    /// </summary>
    private async Task ScoreInlineParameterObjectConstructionAsync(
        Dictionary<string, ClassifiedType> typesByDisplay, Solution solution, ScoreReport report, CancellationToken ct)
    {
        var w = _config.Weight("inlineParameterObjectConstruction");
        if (w == 0) return;

        var siteCountByType = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var project in solution.Projects)
        {
            if (project.Name.Contains("Test", StringComparison.OrdinalIgnoreCase)) continue;
            var compilation = await project.GetCompilationAsync(ct);
            if (compilation is null) continue;

            foreach (var tree in compilation.SyntaxTrees)
            {
                var root = await tree.GetRootAsync(ct);
                var model = compilation.GetSemanticModel(tree);

                foreach (var inv in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    if (inv.ArgumentList is null) continue;
                    foreach (var arg in inv.ArgumentList.Arguments)
                    {
                        if (arg.Expression is not BaseObjectCreationExpressionSyntax oc) continue;
                        if (model.GetTypeInfo(oc, ct).Type is not INamedTypeSymbol t) continue;
                        if (!BoundaryInput.IsBoundaryName(t.Name)) continue;
                        // The rule charges for the construction SHAPE, so the constructor is what
                        // has to be reachable — an exported input type with an internal ctor offers
                        // no other assembly this argument bundle. Only skip when the ctor resolves
                        // and is non-exported: on a degraded build symbols go unresolved, and
                        // dropping those sites would deepen exactly the under-count issue #9 exists
                        // to surface.
                        if (model.GetSymbolInfo(oc, ct).Symbol is IMethodSymbol ctor
                            && !SurfaceVisibility.IsExported(ctor)) continue;
                        int count = Math.Max(oc.ArgumentList?.Arguments.Count ?? 0, oc.Initializer?.Expressions.Count ?? 0);
                        if (count < 4) continue;
                        var key = SolutionClassifier.TypeKey(t);
                        siteCountByType[key] = siteCountByType.GetValueOrDefault(key) + 1;
                    }
                }
            }
        }

        foreach (var (display, sites) in siteCountByType)
        {
            if (!typesByDisplay.TryGetValue(display, out var c)) continue;
            if (!c.IsExported) continue; // an internal input object is not boundary surface
            int pts = Math.Min(25, 5 * sites) * w;
            if (pts == 0) continue;
            AddEntry(report, c.Group, "inlineParameterObjectConstruction", pts, c.Type, c.File, c.Line,
                $"{c.Type.Name} constructed inline at {sites} call site(s)");
        }
    }

    private static string StripAsyncName(string name)
        => name.EndsWith("Async", StringComparison.Ordinal) && name.Length > 5 ? name[..^5] : name;
}
