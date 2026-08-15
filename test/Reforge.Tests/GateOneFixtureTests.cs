namespace Reforge.Tests;

/// <summary>
/// Gate 1 — anti-gaming, enforced by fixture.
///
/// <para>From <c>docs/superpowers/specs/2026-08-15-scoring-alignment-design.md</c>: for each scored
/// rule the sample solution carries a <c>Before</c> that fires it and a <c>CheapestFix</c> — the
/// laziest edit that stops it firing, as an LLM would perform it — and the score must not improve
/// between them. A rule whose cheapest fix lowers the score is a rule an agent can satisfy without
/// improving anything, which is worse than not having the rule: it spends the agent's effort and
/// reports progress for it.</para>
///
/// <para>The spec makes the point that <c>longMethod</c> would have failed this gate on the day it
/// was written. This file is the executable version of that check, so the next rule to fail it
/// fails in CI instead of after a year of shipping.</para>
///
/// <para>Fixtures live in <c>test/SampleSolution/SampleSolution.Gate/Rules/</c>, one pair per
/// label, discovered from disk rather than listed here — a hand-maintained registry of fixtures is
/// the same shape of bug as the hand-maintained command list that made four commands silently
/// print help for four months.</para>
/// </summary>
[Collection("SampleSolution")]
public class GateOneFixtureTests
{
    private const string GateHeader = "// gate1:";

    private readonly SampleSolutionFixture _fixture;

    public GateOneFixtureTests(SampleSolutionFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>
    /// Rules that will not get a Gate 1 fixture, each with the reason it is excused rather than
    /// merely missing. An excuse has to be a property of the rule, not of the schedule.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> Exempt =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // Negative weight: the rule pays code for adopting the section's published read API.
            // "The cheapest fix must not improve the score" is meaningless for a credit — there is
            // nothing to fix. Gaming a credit means claiming it without earning it, which is a
            // different test (CanonicalReadDtoDerivationTests) against a different question.
            ["canonicalReadDtoReturn"] =
                "credit, not a penalty — a cheapest-fix pair does not typecheck as a concept",

            // These fire against a section, not a declaration: AddEntryByName is called with an
            // empty file, so there is no declaring file to attribute the points to and the
            // before/after harness below cannot see them. Gating them needs a section-shaped
            // fixture harness, which is worth building and is not this PR.
            ["missingReadSurface"] = "section-level entry with no declaring file; needs a section-shaped harness",
            ["missingWriteSurface"] = "section-level entry with no declaring file; needs a section-shaped harness",
            ["missingPrimaryInfoDto"] = "section-level entry with no declaring file; needs a section-shaped harness",

            // The spec retires or re-bases all three as part of the internal-axis rework (issue
            // #19). Writing fixtures for a rule that is being deleted spends the effort twice.
            ["longMethod"] = "spec retires this rule; folded into the closure-based complexity measure",
            ["largeClass"] = "spec retires the LOC basis; replaced by the cohesion measure",
            ["cognitiveComplexity"] = "spec re-bases the unit; fixture would pin the basis being replaced",
        };

    /// <summary>
    /// Scored rules with no Gate 1 fixture yet. This list only shrinks. It exists so the coverage
    /// test can fail on a <b>new</b> rule while the retroactive backlog is worked through — an
    /// unlisted rule is a rule someone added without a fixture, and that is the case worth
    /// catching, since it is the only one where the fixture could still have been written first.
    /// </summary>
    private static readonly IReadOnlySet<string> NotYetCovered = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "dtoCollectionProperty",
        "dtoNestedProperty",
        "applicationServiceMethod",
        "readServiceInterfaceMethod",
        "fullServiceInterfaceMethod",
        "repositoryInterfaceMethod",
        "repositoryImplementationMethod",
        "newRepositoryInterface",
        "newRepositoryImplementation",
        "diRegistration",
        "controllerAction",
        "backgroundJob",
        "duplicateDbSetOwner",
        "methodReturnsEntityAcrossSection",
        "publicInputWithHiddenState",
        "parameterBagInput",
        "inlineParameterObjectConstruction",
        "crossSectionReadInterface",
        "crossSectionFullService",
        "crossSectionRepository",
        "writeCapableInterfaceUsedReadOnly",
        "crossSectionWriteSurface",
        "readSurfaceProjectionMethod",
        "booleanParameter",
        "optionsBag",
        "dashboardAdminPageName",
        "oneImplementationInterface",
        "actionDispatcher",
        "genericActionDispatcher",
        "mutationModeParameter",
        "flagsControlFlow",
    };

    // ---------------- The gate ----------------

    [Fact]
    public async Task CheapestFix_NeverLowersTheScore()
    {
        var report = await ScoreAsync();
        var pairs = DiscoverPairs();
        Assert.NotEmpty(pairs);

        var failures = new List<string>();
        foreach (var pair in pairs)
        {
            int before = PointsIn(report, pair.BeforeFile);
            int cheapest = PointsIn(report, pair.CheapestFixFile);
            if (cheapest < before)
                failures.Add(
                    $"{pair.Label}: cheapest fix scores {cheapest}, down from {before}. "
                    + $"Rule(s) {string.Join(", ", pair.Rules)} can be satisfied without improving the design.");
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    /// <summary>
    /// A Before file that does not actually fire the rule it claims gates nothing — the pair would
    /// pass forever on two zeros. Checked separately from the gate so the failure message says
    /// "your fixture is broken" rather than "your rule is gameable".
    /// </summary>
    [Fact]
    public async Task EveryDeclaredRule_ActuallyFiresInItsBeforeFixture()
    {
        var report = await ScoreAsync();

        var failures = new List<string>();
        foreach (var pair in DiscoverPairs())
        {
            var fired = report.Groups.Values
                .SelectMany(g => g.Entries)
                .Where(e => SameFile(e.File, pair.BeforeFile) && e.Points != 0)
                .Select(e => e.Rule)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var rule in pair.Rules)
                if (!fired.Contains(rule))
                    failures.Add(
                        $"{pair.Label}: declares '{rule}' but that rule does not fire in {pair.BeforeFile}. "
                        + $"Rules that did fire: {(fired.Count == 0 ? "(none)" : string.Join(", ", fired.OrderBy(r => r, StringComparer.Ordinal)))}.");
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    /// <summary>
    /// A GoodFix is optional, but where one exists it must actually score better — otherwise the
    /// rule charges for the shape rather than the problem, and following it makes the code worse.
    /// </summary>
    [Fact]
    public async Task GoodFix_WhenPresent_LowersTheScore()
    {
        var report = await ScoreAsync();

        var failures = new List<string>();
        foreach (var pair in DiscoverPairs().Where(p => p.GoodFixFile is not null))
        {
            int before = PointsIn(report, pair.BeforeFile);
            int good = PointsIn(report, pair.GoodFixFile!);
            if (good >= before)
                failures.Add($"{pair.Label}: good fix scores {good}, not below {before}.");
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    // ---------------- The ratchet ----------------

    [Fact]
    public void EveryScoredRule_IsCoveredOrDeclaredUncovered()
    {
        var scored = SurfaceScoreConfig.Default().Weights
            .Where(kv => kv.Value != 0)
            .Select(kv => kv.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var covered = CoveredRules();

        var unaccounted = scored
            .Where(r => !covered.Contains(r) && !Exempt.ContainsKey(r) && !NotYetCovered.Contains(r))
            .OrderBy(r => r, StringComparer.Ordinal)
            .ToList();

        Assert.True(unaccounted.Count == 0,
            "Scored rule(s) with no Gate 1 fixture and no declared exemption: "
            + string.Join(", ", unaccounted)
            + ". Add a Before/CheapestFix pair under test/SampleSolution/SampleSolution.Gate/Rules/, "
            + "or record why the rule cannot have one. A rule that cannot clear Gate 1 does not ship.");
    }

    /// <summary>
    /// The other direction: a rule listed as uncovered or exempt must still be a scored rule. This
    /// is what makes the backlog shrink honestly — deleting a rule, or landing its fixture, without
    /// removing its entry here would leave a list that no longer describes anything.
    /// </summary>
    [Fact]
    public void TheUncoveredList_DescribesRulesThatStillExistAndAreStillUncovered()
    {
        var scored = SurfaceScoreConfig.Default().Weights
            .Where(kv => kv.Value != 0)
            .Select(kv => kv.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var covered = CoveredRules();

        var stale = NotYetCovered.Concat(Exempt.Keys)
            .Where(r => !scored.Contains(r))
            .Select(r => $"{r} (no longer a scored rule)")
            .Concat(NotYetCovered.Where(covered.Contains).Select(r => $"{r} (now has a fixture)"))
            .OrderBy(r => r, StringComparer.Ordinal)
            .ToList();

        Assert.True(stale.Count == 0,
            "Stale entries in this file's Exempt/NotYetCovered lists: " + string.Join(", ", stale));
    }

    // ---------------- Discovery ----------------

    private sealed record FixturePair(string Label, string BeforeFile, string CheapestFixFile, string? GoodFixFile, IReadOnlyList<string> Rules);

    private static string RulesDirectory()
    {
        var testDir = Path.GetDirectoryName(typeof(GateOneFixtureTests).Assembly.Location)!;
        return Path.Combine(SampleSolutionFixture.FindRepoRoot(testDir),
            "test", "SampleSolution", "SampleSolution.Gate", "Rules");
    }

    private static List<FixturePair> DiscoverPairs()
    {
        var dir = RulesDirectory();
        Assert.True(Directory.Exists(dir), $"Gate 1 fixture directory not found at {dir}");

        var pairs = new List<FixturePair>();
        foreach (var beforePath in Directory.EnumerateFiles(dir, "*.Before.cs").OrderBy(p => p, StringComparer.Ordinal))
        {
            var label = Path.GetFileName(beforePath)[..^".Before.cs".Length];
            var cheapestPath = Path.Combine(dir, $"{label}.CheapestFix.cs");
            Assert.True(File.Exists(cheapestPath),
                $"Gate 1 fixture '{label}' has a Before but no CheapestFix. A Before on its own gates nothing.");

            var goodPath = Path.Combine(dir, $"{label}.GoodFix.cs");
            var rules = ParseDeclaredRules(beforePath);
            Assert.True(rules.Count > 0,
                $"Gate 1 fixture '{label}' declares no rules. Add a '{GateHeader} ruleName, ruleName' comment to {label}.Before.cs.");

            pairs.Add(new FixturePair(
                label,
                RelativeSourcePath(label, "Before"),
                RelativeSourcePath(label, "CheapestFix"),
                File.Exists(goodPath) ? RelativeSourcePath(label, "GoodFix") : null,
                rules));
        }
        return pairs;
    }

    private static List<string> ParseDeclaredRules(string path)
    {
        foreach (var line in File.ReadLines(path))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;
            if (!trimmed.StartsWith(GateHeader, StringComparison.Ordinal)) continue;
            return trimmed[GateHeader.Length..]
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();
        }
        return new List<string>();
    }

    private static string RelativeSourcePath(string label, string kind)
        => $"SampleSolution.Gate/Rules/{label}.{kind}.cs";

    /// <summary>
    /// Rules declared by some discovered pair. Deliberately says nothing about whether the pair
    /// passes — <see cref="CheapestFix_NeverLowersTheScore"/> owns that verdict, and a rule with a
    /// failing fixture should be reported as gameable, not as unlisted. Folding the gate result in
    /// here would fail two tests for one cause and put the misleading message first.
    /// </summary>
    private static HashSet<string> CoveredRules()
    {
        var covered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in DiscoverPairs())
            foreach (var rule in pair.Rules)
                covered.Add(rule);
        return covered;
    }

    // ---------------- Scoring ----------------

    private static readonly SemaphoreSlim ScoreLock = new(1, 1);
    private static ScoreReport? _cachedReport;

    /// <summary>
    /// Scores the sample solution once for the whole class. Every test here asks the same question
    /// of the same immutable solution, and scoring it is the expensive part — five tests each
    /// re-running seven analysis passes would dominate the suite's runtime for no added signal.
    /// </summary>
    private async Task<ScoreReport> ScoreAsync()
    {
        if (_cachedReport is not null) return _cachedReport;
        await ScoreLock.WaitAsync();
        try
        {
            if (_cachedReport is null)
            {
                var cfg = SurfaceScoreConfig.Default();
                var engine = new SurfaceScoreEngine(cfg, LocationHelper.GetSolutionDirectory(_fixture.Solution));
                _cachedReport = await engine.ScoreAsync(_fixture.Solution, CancellationToken.None);
            }
            return _cachedReport;
        }
        finally
        {
            ScoreLock.Release();
        }
    }

    /// <summary>
    /// Total points attributed to declarations in one file. Attribution is by declaring file rather
    /// than by type name so a fixture can add supporting types — the parameter object, the nested
    /// DTO — and have their cost counted against the fix that introduced them. That is the whole
    /// point: the cheapest fix's new types are where the surface it "removed" went.
    /// </summary>
    private static int PointsIn(ScoreReport report, string relativeFile)
        => report.Groups.Values
            .SelectMany(g => g.Entries)
            .Where(e => SameFile(e.File, relativeFile))
            .Sum(e => e.Points);

    private static bool SameFile(string entryFile, string relativeFile)
        => entryFile.Replace('\\', '/').EndsWith(relativeFile, StringComparison.OrdinalIgnoreCase);
}
