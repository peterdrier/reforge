using System.Text.Json;

namespace Reforge;

/// <summary>
/// Per-scope result of comparing a current score against a baseline. <see cref="Improvement"/>
/// is the authoritative verdict: a change only counts as progress if it is a Pareto
/// improvement — surface non-worse AND complexity non-worse, with at least one strictly
/// better. A surface drop bought with a complexity rise is a <c>traded</c> verdict, not an
/// improvement, no matter what the (informational) combined total does.
/// </summary>
public sealed record ScopeDelta(
    string Scope,
    int BaseSurface, int NowSurface, int SurfaceDelta,
    int BaseInternal, int NowInternal, int InternalDelta,
    string Verdict,
    bool Improvement);

public sealed class BaselineComparison
{
    public string BaselinePath { get; init; } = "";
    public ScopeDelta Solution { get; set; } = null!;
    public List<ScopeDelta> Groups { get; } = new();
    public List<SuspiciousImprovement> Suspicious { get; } = new();
}

/// <summary>
/// Computes the Pareto gate between a freshly-scored <see cref="ScoreReport"/> and a baseline
/// produced by an earlier <c>surface-score --format json</c> run. Designed to be run
/// per-commit: feed the parent commit's JSON as the baseline so each commit gets its own
/// verdict and the loop feels the counter-signal at the moment it makes a bad trade, rather
/// than having it buried inside a net-positive PR diff.
/// </summary>
public static class SurfaceScoreBaseline
{
    // A trade is only flagged when complexity worsens by BOTH an absolute and a relative
    // margin — otherwise a tiny section where one method moved would spam false suspicions.
    private const int AbsThreshold = 15;
    private const int DispatcherAbsThreshold = 10; // one dispatcher fire is ~20pts

    private static readonly string[] MethodSurfaceRules =
    {
        "applicationServiceMethod", "readServiceInterfaceMethod", "fullServiceInterfaceMethod",
        "repositoryInterfaceMethod", "repositoryImplementationMethod", "controllerAction"
    };
    private static readonly string[] InterfaceMethodRules =
    {
        "readServiceInterfaceMethod", "fullServiceInterfaceMethod", "repositoryInterfaceMethod"
    };
    private static readonly string[] DispatcherRules = { "actionDispatcher", "flagsControlFlow" };
    private static readonly string[] GodMethodRules = { "longMethod", "cognitiveComplexity", "largeClass" };

    public static BaselineComparison Compare(ScoreReport now, string baselineJsonPath)
    {
        var json = File.ReadAllText(baselineJsonPath);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var baseSolution = ReadScope(root);
        var baseGroups = new Dictionary<string, BaselineScope>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("groups", out var groupsEl) && groupsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var g in groupsEl.EnumerateArray())
            {
                if (!g.TryGetProperty("name", out var nameEl)) continue;
                baseGroups[nameEl.GetString() ?? ""] = ReadScope(g);
            }
        }

        var comparison = new BaselineComparison { BaselinePath = baselineJsonPath };

        var nowSolution = new BaselineScope(now.SurfaceTotal, now.InternalComplexityTotal, now.ByRule);
        var allEntries = now.Groups.Values.SelectMany(g => g.Entries).ToList();
        comparison.Solution = Evaluate("solution", baseSolution, nowSolution, allEntries, comparison.Suspicious);

        foreach (var (name, g) in now.Groups)
        {
            var baseScope = baseGroups.TryGetValue(name, out var b) ? b : new BaselineScope(0, 0, new());
            var nowScope = new BaselineScope(g.SurfaceTotal, g.InternalComplexityTotal, g.ByRule);
            comparison.Groups.Add(Evaluate(name, baseScope, nowScope, g.Entries, comparison.Suspicious));
        }
        // Sections that existed in the baseline but produced nothing now (deleted/emptied) —
        // surface dropped to zero, complexity to zero: a clean Pareto improvement, nothing to flag.

        return comparison;
    }

    private static ScopeDelta Evaluate(string scope, BaselineScope b, BaselineScope now,
        IReadOnlyList<ScoreEntry> nowEntries, List<SuspiciousImprovement> sink)
    {
        int dSurface = now.Surface - b.Surface;     // negative = surface improved
        int dInternal = now.Internal - b.Internal;  // positive = complexity worsened

        bool surfaceWorse = dSurface > 0;
        bool internalWorse = dInternal > 0;
        bool surfaceBetter = dSurface < 0;
        bool internalBetter = dInternal < 0;

        string verdict;
        bool improvement;
        if (!surfaceWorse && !internalWorse && (surfaceBetter || internalBetter))
        {
            verdict = "improved"; improvement = true;
        }
        else if (surfaceBetter && internalWorse)
        {
            verdict = "traded"; improvement = false;
        }
        else if (surfaceWorse || internalWorse)
        {
            verdict = "regressed"; improvement = false;
        }
        else
        {
            verdict = "neutral"; improvement = false;
        }

        // A "traded" verdict ALWAYS produces a suspicious entry — surface dropped while
        // complexity rose, which is never a real improvement regardless of magnitude. The
        // message attributes the regression to the specific rules/symbols that rose (so the
        // report says "ApplySignupActionAsync is a generic dispatcher", not "complexity went up").
        if (verdict == "traded")
        {
            var (kind, drivers) = Attribute(b, now, nowEntries);
            sink.Add(new SuspiciousImprovement(scope, kind,
                $"{scope}: surface improved by {-dSurface} points, but implementation complexity worsened by {dInternal} " +
                $"(verdict: traded — not an improvement).{drivers}",
                dSurface, dInternal, false));
        }
        else if (surfaceBetter)
        {
            // Surface improved and the verdict isn't "traded" (complexity didn't worsen net) —
            // but watch for a sneaky composition: dispatchers/god-methods rose while other
            // complexity fell enough to mask it. Early-warning, threshold-gated to avoid spam.
            int dMethodSurface = SumRules(now.ByRule, MethodSurfaceRules) - SumRules(b.ByRule, MethodSurfaceRules);
            int dDispatcher = SumRules(now.ByRule, DispatcherRules) - SumRules(b.ByRule, DispatcherRules);
            if (dMethodSurface < 0 && dDispatcher >= DispatcherAbsThreshold)
            {
                sink.Add(new SuspiciousImprovement(scope, "dispatcher-up-methods-down",
                    $"{scope}: public method surface dropped by {-dMethodSurface} points while dispatcher penalties rose by {dDispatcher}.{Attribute(b, now, nowEntries).Drivers}",
                    dSurface, dInternal, false));
            }

            int dInterface = SumRules(now.ByRule, InterfaceMethodRules) - SumRules(b.ByRule, InterfaceMethodRules);
            int dGod = SumRules(now.ByRule, GodMethodRules) - SumRules(b.ByRule, GodMethodRules);
            if (dInterface < 0 && dGod >= AbsThreshold)
            {
                sink.Add(new SuspiciousImprovement(scope, "godmethod-up-interface-down",
                    $"{scope}: interface-method surface dropped by {-dInterface} points while long/complex-method penalties rose by {dGod}.",
                    dSurface, dInternal, false));
            }
        }

        return new ScopeDelta(scope, b.Surface, now.Surface, dSurface, b.Internal, now.Internal, dInternal, verdict, improvement);
    }

    /// <summary>
    /// Builds the per-rule / per-symbol attribution for a regression: which internal-complexity
    /// rules rose, by how much, and which symbols carry them now. This is the "why" the report
    /// must surface — specific attribution, not just a net number.
    /// </summary>
    private static (string Kind, string Drivers) Attribute(BaselineScope b, BaselineScope now, IReadOnlyList<ScoreEntry> nowEntries)
    {
        var increased = SurfaceScoreRuleGroups.InternalComplexity
            .Select(r => (Rule: r, Delta: now.ByRule.GetValueOrDefault(r) - b.ByRule.GetValueOrDefault(r)))
            .Where(x => x.Delta > 0)
            .OrderByDescending(x => x.Delta)
            .ToList();
        if (increased.Count == 0) return ("complexity-traded-for-surface", "");

        var parts = new List<string>();
        foreach (var (rule, delta) in increased.Take(3))
        {
            var syms = nowEntries.Where(e => e.Rule == rule)
                .OrderByDescending(e => e.Points)
                .Select(e => e.Symbol)
                .Distinct(StringComparer.Ordinal)
                .Take(3)
                .ToList();
            parts.Add(syms.Count > 0 ? $"{rule} +{delta} [{string.Join(", ", syms)}]" : $"{rule} +{delta}");
        }

        var kind = increased.Any(x => x.Rule is "genericActionDispatcher" or "mutationModeParameter" or "actionDispatcher")
            ? "generic-dispatcher-consolidation"
            : "complexity-traded-for-surface";
        return (kind, " Drivers: " + string.Join("; ", parts) + ".");
    }

    private static int SumRules(IReadOnlyDictionary<string, int> byRule, string[] rules)
    {
        int sum = 0;
        foreach (var r in rules)
            if (byRule.TryGetValue(r, out var v)) sum += v;
        return sum;
    }

    private static BaselineScope ReadScope(JsonElement el)
    {
        int total = GetInt(el, "total");
        int surface = el.TryGetProperty("surfaceTotal", out var s) && s.ValueKind == JsonValueKind.Number
            ? s.GetInt32()
            : total; // pre-axis baseline: everything was surface
        int internalC = GetInt(el, "internalComplexityTotal");

        var byRule = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (el.TryGetProperty("byRule", out var br) && br.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in br.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.Number)
                    byRule[p.Name] = p.Value.GetInt32();
        }
        return new BaselineScope(surface, internalC, byRule);
    }

    private static int GetInt(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;

    private sealed record BaselineScope(int Surface, int Internal, Dictionary<string, int> ByRule);
}
