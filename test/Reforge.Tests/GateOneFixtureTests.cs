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
/// <para>That is two claims, and both are asserted. The cheapest fix must <b>look like a fix</b> —
/// the declared rule has to charge strictly less in it than in the Before, or an agent would never
/// have made the edit and the fixture is demonstrating nothing. Only then does "the total did not
/// drop" mean anything: without the first half, an unchanged copy of the Before file passes the
/// gate and the rule is recorded as covered forever.</para>
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

            // Everything ScoreSectionArchitecture emits. These fire on what the *section* looks
            // like — which types happen to coexist in it — not on the declaration they land on, so
            // this harness cannot gate them however they are attributed. The first three are
            // recorded with an empty file and vanish from the comparison entirely; the last two
            // carry a real file and are worse, because they look like ordinary per-declaration
            // points while actually being a function of the other fixtures in the section.
            ["missingReadSurface"] = "section-coupled; needs the isolated-variant harness",
            ["missingWriteSurface"] = "section-coupled; needs the isolated-variant harness",
            ["missingPrimaryInfoDto"] = "section-coupled; needs the isolated-variant harness",
            ["readSurfaceProjectionMethod"] = "section-coupled; needs the isolated-variant harness",
            ["crossSectionWriteSurface"] = "section-coupled; needs the isolated-variant harness",

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
        // Withdrawn rather than never written: the nesting dodge that looked like a cheapest fix
        // moves scalars between types without reducing dtoScalarProperty's own charge, so it is not
        // a fix an agent would make. Two dodges that would reduce it — collapsing the scalars into
        // one collection property, or hoisting them to an unclassified base class that
        // GetMembers() does not see — are unverified, and a suspected-red gate is not something to
        // ship on a hunch. See CHANGELOG for the reasoning.
        "dtoScalarProperty",
        "dtoCollectionProperty",
        "dtoNestedProperty",
        "publicDtoType",
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
    /// The other half of the gate. <see cref="CheapestFix_NeverLowersTheScore"/> compares totals,
    /// and a total comparison alone cannot tell a cheapest fix from a file that was never fixed:
    /// an unchanged copy of the Before scores identically and passes, and so does an incomplete fix
    /// that still fires the rule but happens to carry enough incidental penalties. Either way
    /// <see cref="CoveredRules"/> goes on counting the rule as gated.
    ///
    /// <para>So the declared rule must charge strictly less in the CheapestFix. That is the
    /// definition of the thing being modelled — the cheapest fix is what an agent does when a rule
    /// fires at it, and an agent does not ship an edit that leaves the rule charging what it did
    /// before. The number the agent is watching has to go down; the gate's job is to prove the
    /// total does not follow it down.</para>
    ///
    /// <para>Strictly-less rather than zero, because rules count as well as trip. A predicate rule
    /// like <c>tupleReturn</c> does go to zero, but <c>dtoScalarProperty</c> charges per property
    /// and a fix that removes two of six is a real reduction that "absent from the CheapestFix"
    /// would reject as impossible.</para>
    /// </summary>
    [Fact]
    public async Task EveryDeclaredRule_ChargesLessInItsCheapestFix()
    {
        var report = await ScoreAsync();

        var failures = new List<string>();
        foreach (var pair in DiscoverPairs())
        {
            foreach (var rule in pair.Rules)
            {
                int before = PointsForRule(report, pair.BeforeFile, rule);
                int cheapest = PointsForRule(report, pair.CheapestFixFile, rule);
                if (cheapest < before) continue;

                failures.Add(
                    $"{pair.Label}: '{rule}' charges {cheapest} in the cheapest fix, "
                    + $"not less than the {before} it charges in the Before. "
                    + (cheapest == before
                        ? "The fix does not move the number the agent is optimizing, so no agent would make it — "
                        : "The fix makes the rule charge more, so no agent would make it — ")
                    + "the pair proves nothing about whether the rule is gameable. Write a cheapest fix that "
                    + "reduces this rule, or withdraw the pair and list the rule as not-yet-covered.");
            }
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

    /// <summary>
    /// Every rule <c>ScoreSectionArchitecture</c> emits. These fire on the shape of the
    /// <i>section</i> — which types coexist in it — rather than on the declaration the points land
    /// against, so a fixture's score stops being a property of the fixture. Kept as a list because
    /// it is a closed set owned by one file; when that file learns a new rule, this list is the
    /// thing to update, and <see cref="EveryRule_BelongsToExactlyOneBucket_AndEveryEntryStillDescribesSomethingTrue"/>
    /// will notice if a name here stops being a real rule.
    /// </summary>
    private static readonly IReadOnlySet<string> SectionCoupled = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "missingReadSurface",
        "missingWriteSurface",
        "missingPrimaryInfoDto",
        "readSurfaceProjectionMethod",
        "crossSectionWriteSurface",
    };

    /// <summary>
    /// The harness reconstructs each variant's score by filtering one shared report to one file.
    /// That is exact for per-declaration rules and wrong for section-coupled ones, in two different
    /// ways, and both are silent.
    ///
    /// <para>The <c>missing*</c> rules are recorded against the section with an <b>empty</b> file,
    /// so the filter discards them: a fixture that declares a repository makes the whole section
    /// repo-backed, turns those rules on for every pair at once, and none of it appears in any
    /// pair's total.</para>
    ///
    /// <para>The other two are the harder case, because they <b>do</b> carry a file and so look
    /// like ordinary per-declaration points. A fixture declaring a conventionally-named
    /// <c>…Info</c> type can become the section's primary DTO in <c>SectionShapeAnalyzer</c>, after
    /// which <c>readSurfaceProjectionMethod</c> is charged against <i>other</i> fixtures' files.
    /// The filter attributes those correctly and reads them wrongly: the number moves because of a
    /// file the pair's author never saw.</para>
    ///
    /// <para>The real fix is to score each variant in its own solution — see issue #26. Until then
    /// this fails the moment a Gate fixture scores through section state, so the gate breaks rather
    /// than quietly measuring something that is not the variant's score.</para>
    /// </summary>
    [Fact]
    public async Task NoGateFixture_ScoresThroughSectionState()
    {
        var report = await ScoreAsync();
        var gateEntries = report.Groups
            .Where(g => g.Key.Equals("Gate", StringComparison.OrdinalIgnoreCase))
            .SelectMany(g => g.Value.Entries)
            .Where(e => e.Points != 0)
            .ToList();

        var problems = gateEntries
            .Where(e => string.IsNullOrWhiteSpace(e.File) || SectionCoupled.Contains(e.Rule))
            .Select(e => $"  {e.Rule} ({e.Points:+#;-#;0}) on "
                + (string.IsNullOrWhiteSpace(e.File) ? "(no file)" : e.File)
                + $" — {e.Detail ?? e.Symbol}")
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

        Assert.True(problems.Count == 0,
            "The Gate section scores points that depend on which fixtures coexist in it, so a "
            + "pair's before/after comparison is no longer a property of that pair:"
            + Environment.NewLine + string.Join(Environment.NewLine, problems) + Environment.NewLine
            + "Most likely a fixture declared a repository (making the section repo-backed) or a "
            + "conventionally-named type that SectionShapeAnalyzer adopted as the section's primary "
            + "DTO. Rules of this shape need each variant scored in its own solution (#26); this "
            + "harness cannot gate them, and the fixture that triggered this belongs elsewhere.");
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
    /// The other direction. Every rule belongs to exactly one bucket — covered, exempt, or
    /// not-yet-covered — and each bucket's entries must still describe something true. Without
    /// this, the lists rot in the one direction the forward check cannot see: the forward check
    /// only asks whether a rule is accounted for <i>somewhere</i>, so a rule that gains a fixture
    /// while keeping its old entry stays green while the entry becomes a lie.
    ///
    /// <para>The exempt bucket is the one that matters most here. Its entries carry a reason, and
    /// a reason is a claim — "this rule cannot have a cheapest-fix pair". The day someone builds
    /// the section-shaped harness and gates <c>missingReadSurface</c>, that claim is false, and a
    /// stale exemption would go on excusing every future rule of the same shape.</para>
    /// </summary>
    [Fact]
    public void EveryRule_BelongsToExactlyOneBucket_AndEveryEntryStillDescribesSomethingTrue()
    {
        var scored = SurfaceScoreConfig.Default().Weights
            .Where(kv => kv.Value != 0)
            .Select(kv => kv.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var covered = CoveredRules();
        var problems = new List<string>();

        foreach (var rule in NotYetCovered.Concat(Exempt.Keys).Distinct(StringComparer.OrdinalIgnoreCase))
            if (!scored.Contains(rule))
                problems.Add($"{rule}: listed here but is not a scored rule — delete the entry.");

        foreach (var rule in NotYetCovered.Where(Exempt.ContainsKey))
            problems.Add($"{rule}: listed as both exempt and not-yet-covered — it is one or the other.");

        foreach (var rule in covered.Where(NotYetCovered.Contains))
            problems.Add($"{rule}: has a fixture but is still listed as not-yet-covered — delete the entry.");

        foreach (var rule in covered.Where(Exempt.ContainsKey))
            problems.Add($"{rule}: has a fixture but is still listed as exempt (\"{Exempt[rule]}\") — "
                + "that reason is now false, so delete the exemption rather than leaving it to excuse the next rule.");

        problems.Sort(StringComparer.Ordinal);
        Assert.True(problems.Count == 0,
            "Bucket assignments in this file are stale or overlapping:" + Environment.NewLine
            + string.Join(Environment.NewLine, problems));
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

    /// <summary>
    /// Points one rule charges against declarations in one file. Same attribution as
    /// <see cref="PointsIn"/>, narrowed to a rule, so the gate can ask the two separate questions:
    /// did the declared rule get cheaper, and did the total stay put.
    /// </summary>
    private static int PointsForRule(ScoreReport report, string relativeFile, string rule)
        => report.Groups.Values
            .SelectMany(g => g.Entries)
            .Where(e => e.Rule.Equals(rule, StringComparison.OrdinalIgnoreCase) && SameFile(e.File, relativeFile))
            .Sum(e => e.Points);

    private static bool SameFile(string entryFile, string relativeFile)
        => entryFile.Replace('\\', '/').EndsWith(relativeFile, StringComparison.OrdinalIgnoreCase);
}
