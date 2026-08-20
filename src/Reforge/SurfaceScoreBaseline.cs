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
    /// <summary>True when the baseline JSON predates conservationAnchors (v0.19+); coverage degrades to ambiguous.</summary>
    public bool BaselineAnchorsMissing { get; set; }
    /// <summary>Per-section conservation verdicts (the gate's roll-up + per-method evidence audit trail).</summary>
    public List<ConservationVerdict> ConservationVerdicts { get; } = new();
    /// <summary>
    /// True when the baseline's build health (degraded/appearsUnbuilt) does not match the current
    /// run's — including when the baseline JSON predates the <c>build</c> block (v0.20+) and so its
    /// state is unknown rather than known-clean. An unbuilt/degraded workspace under-resolves
    /// cross-section/DI/entity-return rules (see issue #9), so a mismatch means the comparison may
    /// be off by several percent and should be treated as low-confidence, not refused.
    /// </summary>
    public bool BuildStateMismatch { get; set; }
    /// <summary>Human-readable explanation naming both build states. Null when no mismatch.</summary>
    public string? BuildStateMismatchMessage { get; set; }
}

/// <summary>Per-removed-method audit row behind a conservation verdict.</summary>
public sealed record MethodEvidence(
    string RemovedMethod,
    string CoverageKind,         // existingDtoFact|addedDtoFact|documentedShard|helper|uncovered|ambiguous
    string? TargetDto,           // primaryInfoDto|cacheDto|settingsInfoDto|readShard|null
    IReadOnlyList<string> CoveredBy,
    IReadOnlyList<MissingInfoFact> MissingInfoFacts);

/// <summary>Section-scoped conservation verdict: the roll-up kind + the evidence rows behind it.</summary>
public sealed record ConservationVerdict(
    string Section,
    string Kind,                 // canonical-consolidation|helperExtractionNoConceptDeleted|capability-evaporation
    bool Improvement,
    string Message,
    IReadOnlyList<MethodEvidence> Methods);

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
    private static readonly string[] GodMethodRules = { "cognitiveComplexity", "largeClass" };
    private static readonly string[] ParamBagRules = { "publicInputWithHiddenState", "parameterBagInput", "inlineParameterObjectConstruction" };

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

        var baseBuildState = ReadBuildState(root);
        var nowBuildState = new BuildState(true, now.BuildHealth.Degraded, now.BuildHealth.AppearsUnbuilt);
        if (!baseBuildState.Known || baseBuildState.Degraded != nowBuildState.Degraded
            || baseBuildState.AppearsUnbuilt != nowBuildState.AppearsUnbuilt)
        {
            comparison.BuildStateMismatch = true;
            comparison.BuildStateMismatchMessage =
                $"baseline was captured on {Describe(baseBuildState)} but the current run {DescribeVerb(nowBuildState)}; " +
                "the comparison may be off by several percent, concentrated in crossSection*/diRegistration/methodReturnsEntity rules.";
        }

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

        // Conservation gate: classify what happened to removed read/service behavior per section.
        var baseAnchors = ReadAnchors(root);
        var baseHelpers = ReadHelperDisplays(root);
        comparison.BaselineAnchorsMissing = !(root.TryGetProperty("conservationAnchors", out var caEl)
            && caEl.ValueKind == JsonValueKind.Array);
        RunConservationGate(now, baseAnchors, baseHelpers, comparison);

        return comparison;
    }

    // ---------------- Baseline build-state guard ----------------

    /// <summary><see cref="Known"/> is false when the baseline JSON predates the <c>build</c>
    /// block (pre-v0.20) — treated as an unknown state, never as implicitly clean.</summary>
    private sealed record BuildState(bool Known, bool Degraded, bool AppearsUnbuilt);

    private static BuildState ReadBuildState(JsonElement root)
    {
        if (!root.TryGetProperty("build", out var b) || b.ValueKind != JsonValueKind.Object)
            return new BuildState(false, false, false);
        bool degraded = b.TryGetProperty("degraded", out var d) && d.ValueKind == JsonValueKind.True;
        bool appearsUnbuilt = b.TryGetProperty("appearsUnbuilt", out var a) && a.ValueKind == JsonValueKind.True;
        return new BuildState(true, degraded, appearsUnbuilt);
    }

    /// <summary>Noun-phrase description of a build state, for "baseline was captured on ...".</summary>
    private static string Describe(BuildState s)
    {
        if (!s.Known) return "an unknown build state (baseline predates build-health tracking)";
        if (s.Degraded) return $"a degraded workspace (compile errors present, appearsUnbuilt={(s.AppearsUnbuilt ? "true" : "false")})";
        if (s.AppearsUnbuilt) return "a degraded/unbuilt workspace (appearsUnbuilt=true)";
        return "a cleanly-compiled workspace";
    }

    /// <summary>Verb-phrase description of a build state, for "the current run ...".</summary>
    private static string DescribeVerb(BuildState s)
    {
        if (s.Degraded) return $"did not compile cleanly (compile errors present, appearsUnbuilt={(s.AppearsUnbuilt ? "true" : "false")})";
        if (s.AppearsUnbuilt) return "appears unbuilt (appearsUnbuilt=true)";
        return "compiled cleanly";
    }

    // ---------------- Conservation gate: baseline anchor/helper parsing ----------------

    /// <summary>Per-section consolidation-target inventories read from a baseline's conservationAnchors.</summary>
    private sealed class SectionAnchors
    {
        public List<string> PrimaryPaths { get; } = new();
        public List<string> SettingsPaths { get; } = new();
        public List<string> CachePaths { get; } = new();
        public List<string> ShardMethods { get; } = new();
        public List<(string Name, string Returns)> InterfaceMethods { get; } = new();
    }

    private static Dictionary<string, SectionAnchors> ReadAnchors(JsonElement root)
    {
        var bySection = new Dictionary<string, SectionAnchors>(StringComparer.OrdinalIgnoreCase);
        if (!root.TryGetProperty("conservationAnchors", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return bySection;

        foreach (var a in arr.EnumerateArray())
        {
            var section = a.TryGetProperty("section", out var s) ? s.GetString() ?? "" : "";
            var role = a.TryGetProperty("role", out var r) ? r.GetString() ?? "" : "";
            if (!bySection.TryGetValue(section, out var sa)) { sa = new SectionAnchors(); bySection[section] = sa; }

            var paths = a.TryGetProperty("paths", out var p) && p.ValueKind == JsonValueKind.Array
                ? p.EnumerateArray().Select(x => x.GetString() ?? "").ToList()
                : new List<string>();

            switch (role)
            {
                case "primaryInfoDto": sa.PrimaryPaths.AddRange(paths); break;
                case "settingsInfoDto": sa.SettingsPaths.AddRange(paths); break;
                case "cacheDto": sa.CachePaths.AddRange(paths); break;
                case "readServiceInterface":
                case "fullServiceInterface":
                    foreach (var (name, returns) in ReadMethods(a))
                        sa.InterfaceMethods.Add((name, returns));
                    break;
                case "readShard":
                    foreach (var (name, _) in ReadMethods(a))
                        sa.ShardMethods.Add(name);
                    break;
            }
        }
        return bySection;
    }

    private static IEnumerable<(string Name, string Returns)> ReadMethods(JsonElement anchor)
    {
        if (!anchor.TryGetProperty("methods", out var ms) || ms.ValueKind != JsonValueKind.Array) yield break;
        foreach (var m in ms.EnumerateArray())
            yield return (
                m.TryGetProperty("name", out var mn) ? mn.GetString() ?? "" : "",
                m.TryGetProperty("returns", out var mr) ? mr.GetString() ?? "" : "");
    }

    private static HashSet<string> ReadHelperDisplays(JsonElement root)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (root.TryGetProperty("helperCandidates", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var h in arr.EnumerateArray())
                if (h.TryGetProperty("display", out var d) && d.GetString() is { } disp)
                    set.Add(disp);
        return set;
    }

    // ---------------- Conservation gate: decision tree ----------------

    /// <summary>
    /// For each section that lost read/service methods (baseline interface anchor minus current),
    /// classify the change: a NEW helper absorbed a removed method (checked FIRST, beats ambiguity)
    /// -> helperExtractionNoConceptDeleted; every removed fact covered by the primary/cache/settings
    /// DTO inventory or a documented shard -> canonical-consolidation; a removed fact definitively
    /// uncovered -> capability-evaporation; remaining uncertainty -> canonical-consolidation under
    /// the ambiguity bias, with the ambiguous facts surfaced as advisory missingInfoFacts.
    /// </summary>
    private static void RunConservationGate(ScoreReport now,
        Dictionary<string, SectionAnchors> baseAnchors, HashSet<string> baseHelpers, BaselineComparison cmp)
    {
        var nowBySection = now.ConservationAnchors
            .GroupBy(a => a.Section, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        // NEW helper method names (present now, absent in the baseline), stripped of Async.
        var newHelperMethods = new HashSet<string>(StringComparer.Ordinal);
        foreach (var h in now.HelperCandidates)
            if (!baseHelpers.Contains(h.Display))
                foreach (var m in h.Methods) newHelperMethods.Add(StripAsync(m));

        foreach (var (section, baseSa) in baseAnchors)
        {
            var nowForSection = nowBySection.TryGetValue(section, out var na) ? na : new List<ConservationAnchor>();
            var nowMethodNames = nowForSection
                .Where(a => a.Role is "readServiceInterface" or "fullServiceInterface")
                .SelectMany(a => a.Methods.Select(m => m.Name))
                .ToHashSet(StringComparer.Ordinal);

            var removed = baseSa.InterfaceMethods.Where(m => !nowMethodNames.Contains(m.Name)).ToList();
            if (removed.Count == 0) continue; // read surface did not drop via method removal

            var nowPrimary = Paths(nowForSection, "primaryInfoDto");
            var nowSettings = Paths(nowForSection, "settingsInfoDto");
            var nowCache = Paths(nowForSection, "cacheDto");
            var nowShards = nowForSection.Where(a => a.Role == "readShard").SelectMany(a => a.Methods.Select(m => m.Name)).ToList();

            // Helper absorption first — a sideways move must not be laundered into consolidation.
            var helperAbsorbed = removed.Where(m => newHelperMethods.Contains(StripAsync(m.Name))).ToList();
            var evidence = new List<MethodEvidence>();

            if (helperAbsorbed.Count > 0)
            {
                foreach (var m in removed)
                    evidence.Add(helperAbsorbed.Contains(m)
                        ? new MethodEvidence(m.Name, "helper", null, Array.Empty<string>(), Array.Empty<MissingInfoFact>())
                        : Cover(m, baseSa, nowPrimary, nowSettings, nowCache, nowShards));
                var moved = string.Join(", ", helperAbsorbed.Select(m => m.Name));
                cmp.ConservationVerdicts.Add(new ConservationVerdict(section, "helperExtractionNoConceptDeleted", false,
                    $"{section}: {helperAbsorbed.Count} read/service method(s) moved into a new helper instead of being deleted ({moved}).", evidence));
                cmp.Suspicious.Add(new SuspiciousImprovement(section, "helperExtractionNoConceptDeleted",
                    $"{section}: read/service surface dropped but {helperAbsorbed.Count} method(s) moved sideways into a new stateless helper ({moved}) — concept not deleted.",
                    0, 0, false));
                continue;
            }

            foreach (var m in removed)
                evidence.Add(Cover(m, baseSa, nowPrimary, nowSettings, nowCache, nowShards));

            bool anyUncovered = evidence.Any(e => e.CoverageKind == "uncovered");
            bool allCovered = evidence.All(e => e.CoverageKind is "existingDtoFact" or "addedDtoFact" or "documentedShard");

            if (allCovered)
            {
                cmp.ConservationVerdicts.Add(new ConservationVerdict(section, "canonical-consolidation", true,
                    $"{section}: read surface consolidated into the canonical DTOs (-{removed.Count} method(s)); all removed facts covered.", evidence));
                cmp.Suspicious.Add(new SuspiciousImprovement(section, "canonical-consolidation",
                    $"{section}: read surface consolidated into the canonical DTOs (-{removed.Count} method(s)); facts covered.", 0, 0, true));
            }
            else if (anyUncovered)
            {
                var lost = string.Join(", ", evidence.Where(e => e.CoverageKind == "uncovered").Select(e => e.RemovedMethod));
                cmp.ConservationVerdicts.Add(new ConservationVerdict(section, "capability-evaporation", false,
                    $"{section}: read surface dropped but removed facts are uncovered ({lost}) — capability evaporated or leaked to callers.", evidence));
                cmp.Suspicious.Add(new SuspiciousImprovement(section, "capability-evaporation",
                    $"{section}: read surface dropped but {lost} not covered by any consolidation target.", 0, 0, false));
            }
            else
            {
                cmp.ConservationVerdicts.Add(new ConservationVerdict(section, "canonical-consolidation", true,
                    $"{section}: read surface consolidated (-{removed.Count} method(s)); some coverage ambiguous (see missingInfoFacts).", evidence));
                cmp.Suspicious.Add(new SuspiciousImprovement(section, "canonical-consolidation",
                    $"{section}: read surface consolidated (-{removed.Count} method(s)); some facts ambiguous, surfaced as advisory.", 0, 0, true));
            }
        }
    }

    private static List<string> Paths(List<ConservationAnchor> anchors, string role)
        => anchors.Where(a => a.Role == role).SelectMany(a => a.Paths).ToList();

    private static string StripAsync(string n)
        => n.EndsWith("Async", StringComparison.Ordinal) && n.Length > 5 ? n[..^5] : n;

    /// <summary>Best-effort coverage of one removed method against the current consolidation inventories. Never scored.</summary>
    private static MethodEvidence Cover((string Name, string Returns) m, SectionAnchors baseSa,
        List<string> nowPrimary, List<string> nowSettings, List<string> nowCache, List<string> nowShards)
    {
        var token = FactToken(m.Name);
        bool settingsy = LooksSettings(m.Name) || IsScalar(m.Returns);

        if (settingsy && nowSettings.Any(p => Contains(p, token)))
            return new MethodEvidence(m.Name, baseSa.SettingsPaths.Any(p => Contains(p, token)) ? "existingDtoFact" : "addedDtoFact",
                "settingsInfoDto", nowSettings.Where(p => Contains(p, token)).ToList(), Array.Empty<MissingInfoFact>());

        if (nowPrimary.Any(p => Contains(p, token)))
            return new MethodEvidence(m.Name, baseSa.PrimaryPaths.Any(p => Contains(p, token)) ? "existingDtoFact" : "addedDtoFact",
                "primaryInfoDto", nowPrimary.Where(p => Contains(p, token)).ToList(), Array.Empty<MissingInfoFact>());

        if (nowCache.Any(p => Contains(p, token)))
            return new MethodEvidence(m.Name, "addedDtoFact", "cacheDto", nowCache.Where(p => Contains(p, token)).ToList(), Array.Empty<MissingInfoFact>());

        if (nowShards.Any(sm => Contains(sm, token) || StripAsync(sm) == StripAsync(m.Name)))
            return new MethodEvidence(m.Name, "documentedShard", "readShard", nowShards.Where(sm => Contains(sm, token)).ToList(), Array.Empty<MissingInfoFact>());

        // Not covered. A removed PRIMITIVE read (returns the Info DTO) is a real capability loss ->
        // uncovered. A charged-shape read (bool/scalar/non-primary DTO) is derivable-but-unproven ->
        // ambiguous (lean consolidation, but surface the fact as advisory).
        var target = settingsy ? "settingsInfoDto" : "primaryInfoDto";
        var facts = new[] { new MissingInfoFact($"{target}.{token}", target) };
        return ReturnsInfoDto(m.Returns)
            ? new MethodEvidence(m.Name, "uncovered", target, Array.Empty<string>(), facts)
            : new MethodEvidence(m.Name, "ambiguous", target, Array.Empty<string>(), facts);
    }

    private static bool Contains(string path, string token)
        => token.Length > 0 && path.Contains(token, StringComparison.OrdinalIgnoreCase);

    private static bool LooksSettings(string name)
        => name.Contains("Settings", StringComparison.OrdinalIgnoreCase)
        || name.Contains("LockDate", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Year", StringComparison.OrdinalIgnoreCase);

    private static bool IsScalar(string returns)
    {
        var r = returns.Replace("Task<", "").Replace("ValueTask<", "").TrimEnd('>').Trim();
        return r is "bool" or "int" or "long" or "string" or "Guid" or "DateTime" or "DateTimeOffset" or "decimal" or "double";
    }

    private static bool ReturnsInfoDto(string returns)
        => returns.Contains("Info", StringComparison.Ordinal) && !IsScalar(returns);

    private static string FactToken(string method)
    {
        var n = StripAsync(method);
        foreach (var pre in new[] { "Get", "Find", "Load", "Build", "Is", "Has" })
            if (n.StartsWith(pre, StringComparison.Ordinal) && n.Length > pre.Length) { n = n[pre.Length..]; break; }
        foreach (var suf in new[] { "ForYearAsync", "ForYear", "BySlugAsync", "BySlug", "ById", "Async" })
            if (n.EndsWith(suf, StringComparison.Ordinal) && n.Length > suf.Length) n = n[..^suf.Length];
        return n;
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

        // Parameter-bag consolidation: methodParameterOverflow fell but equivalent input/command
        // surface was introduced. Flagged regardless of verdict — the trade can keep surface flat
        // (the bag offsets the param reduction) and need not touch internal complexity at all.
        int dOverflow = now.ByRule.GetValueOrDefault("methodParameterOverflow") - b.ByRule.GetValueOrDefault("methodParameterOverflow");
        int dParamBag = SumRules(now.ByRule, ParamBagRules) - SumRules(b.ByRule, ParamBagRules);
        if (dOverflow < 0 && dParamBag > 0)
        {
            var types = nowEntries.Where(e => ParamBagRules.Contains(e.Rule, StringComparer.Ordinal))
                .OrderByDescending(e => e.Points)
                .Select(e => e.Symbol)
                .Distinct(StringComparer.Ordinal)
                .Take(3)
                .ToList();
            var typeStr = types.Count > 0 ? $": {string.Join(", ", types)}" : "";
            sink.Add(new SuspiciousImprovement(scope, "parameter-bag-consolidation",
                $"{scope}: method parameter score decreased by {-dOverflow}, but equivalent input/parameter-bag surface was introduced (+{dParamBag}){typeStr}.",
                dSurface, dInternal, false));
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

        var kind = increased.Any(x => x.Rule is "mutationModeParameter" or "actionDispatcher")
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
