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
    /// </summary>
    private async Task ScoreContentsAsync(Solution solution, List<ClassifiedType> classified, ScoreReport report,
        HashSet<string> analyzedAssemblies, CallPathFold fold, CancellationToken ct)
    {
        var complexityW = _config.Weight("methodComplexity");
        var countW = _config.Weight("methodCount");

        var contents = classified
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
