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
/// <para>The spec makes the point that a length rule would have failed this gate on the day it
/// was written. This file is the executable version of that check, so the next rule to fail it
/// fails in CI instead of after a year of shipping.</para>
///
/// <para>A rule that fails gets a pair too, marked <c>// gate1-gameable:</c>, and the assertion
/// inverts: the drop is required rather than forbidden (<see
/// cref="KnownGameableFixtures_StillLowerTheScore"/>). Without somewhere to put a failing rule the
/// only way to record one is a red build, and a red build is not a finding — it is a blocked branch,
/// and blocked branches get unblocked by tuning the fixture until it passes. That is the gate
/// quietly becoming the thing it was built to catch, so the failures are made into data instead.</para>
///
/// <para>Each variant is compiled and scored <b>alone</b> (<see cref="IsolatedVariantScorer"/>), so
/// a variant's score is the whole report rather than a filtered slice of a shared one. The earlier
/// filtering approach could not see section-level rules at all and mis-read the ones that carried a
/// file, which meant a fixture's number could move because of a different fixture entirely; issue
/// #26 has the three ways that went wrong. A variant may span sections by carrying satellite files
/// (<c>&lt;label&gt;.&lt;variant&gt;.&lt;Section&gt;.cs</c>), without which no cross-section rule
/// could ever fire in a fixture and five backlog entries would be unreachable by construction.</para>
///
/// <para>Fixtures live in <c>test/SampleSolution/SampleSolution.Gate/Rules/</c>, one pair per
/// label, discovered from disk rather than listed here — a hand-maintained registry of fixtures is
/// the same shape of bug as the hand-maintained command list that made four commands silently
/// print help for four months.</para>
/// </summary>
public class GateOneFixtureTests
{
    private const string GateHeader = "// gate1:";
    private const string GameableHeader = "// gate1-gameable:";
    private const string GateAssembly = "SampleSolution.Gate";

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

            // Cost, not doubt: the threshold is 750 nonblank LOC, so a Before/CheapestFix pair is
            // ~1,600 lines of synthetic class to demonstrate one 10-point charge. The rule's Gate 1
            // reasoning is measured in 2026-08-20-internal-axis-second-corpus.md instead — the
            // partial-split escape is closed by summing partial declarations (see
            // GeneratedPartialScoringTests), and the remaining one costs a constructor partition.
            ["largeClass"] = "a 750-LOC threshold makes a fixture pair ~1,600 lines of synthetic class",
        };

    /// <summary>
    /// Scored rules with no Gate 1 fixture yet. This list only shrinks. It exists so the coverage
    /// test can fail on a <b>new</b> rule while the retroactive backlog is worked through — an
    /// unlisted rule is a rule someone added without a fixture, and that is the case worth
    /// catching, since it is the only one where the fixture could still have been written first.
    ///
    /// <para>Everything on this list is also a rule nobody has asked to fire.
    /// <see cref="EveryDeclaredRule_ActuallyFiresInItsBeforeFixture"/> only checks rules a pair
    /// declares, so a rule that is broken outright looks exactly like a rule that is merely
    /// unfixtured. <c>diRegistration</c> was the first one caught that way: it had been dead since
    /// it shipped — the classification lookup used a bare display string against a dictionary keyed
    /// on assembly-qualified names — and scored zero against 452 registrations in Humans without
    /// anything going red. Each entry below is a rule whose behaviour is currently unverified in
    /// both directions, not just ungated.</para>
    /// </summary>
    private static readonly IReadOnlySet<string> NotYetCovered = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "readServiceInterfaceMethod",
        "fullServiceInterfaceMethod",
        "repositoryInterfaceMethod",
        "repositoryImplementationMethod",
        "newRepositoryInterface",
        "newRepositoryImplementation",
        "controllerAction",
        "backgroundJob",
        "duplicateDbSetOwner",
        "publicInputWithHiddenState",
        "parameterBagInput",
        "inlineParameterObjectConstruction",
        "writeCapableInterfaceUsedReadOnly",
        "oneImplementationInterface",

        // Section-shape rules. These were exempt while the harness scored one shared solution: they
        // charge a section for its shape, and a filtered slice of a shared report either could not
        // see them (recorded with no file) or attributed them to whichever fixture they landed on.
        // Isolation removed that blocker — a variant compiled alone IS a section — so they now
        // measure correctly and are merely unwritten. Writing them needs a fixture that establishes
        // a section shape, which is more than a type per file.
        "readSurfaceProjectionMethod",

        // Cross-section rules: they only fire when the consumer and the dependency are in
        // different sections, and sections come from assembly names. A one-project variant has one
        // section by construction, so these were unfixturable in a way that no amount of fixture
        // writing would have fixed — a defect in the harness wearing a backlog entry's clothes.
        // A variant can now declare satellite sections (see IsolatedVariantScorer.SatellitesOf and
        // IsolatedVariantScorerTests), so what is left here is the writing.
        //
        // crossSectionReadInterface and crossSectionFullService have pairs now. The one below does
        // not:
        //
        // crossSectionRepository needs a *Repository in the satellite, and inside the FULL sample
        // solution every satellite file compiles into SampleSolution.Gate — which would make Gate
        // repo-backed and switch the missing* rules on for every other Gate 1 fixture's
        // neighbourhood. That is why the harness's own probe uses a service. Fixturing it needs the
        // satellite to be excluded from the full-solution build first, which is a harness change.
    };

    // ---------------- The gate ----------------

    [Fact]
    public async Task CheapestFix_NeverLowersTheScore()
    {
        var pairs = DiscoverPairs().Where(p => p.GameableNote is null).ToList();
        Assert.NotEmpty(pairs);

        var failures = new List<string>();
        foreach (var pair in pairs)
        {
            int before = (await ScoreAsync(pair.BeforeFile)).Total;
            int cheapest = (await ScoreAsync(pair.CheapestFixFile)).Total;
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
        var failures = new List<string>();
        foreach (var pair in DiscoverPairs())
        {
            var before = await ScoreAsync(pair.BeforeFile);
            var cheapest = await ScoreAsync(pair.CheapestFixFile);

            foreach (var rule in pair.Rules)
            {
                int was = before.ByRule.GetValueOrDefault(rule);
                int now = cheapest.ByRule.GetValueOrDefault(rule);
                if (now < was) continue;

                failures.Add(
                    $"{pair.Label}: '{rule}' charges {now} in the cheapest fix, "
                    + $"not less than the {was} it charges in the Before. "
                    + (now == was
                        ? "The fix does not move the number the agent is optimizing, so no agent would make it — "
                        : "The fix makes the rule charge more, so no agent would make it — ")
                    + "the pair proves nothing about whether the rule is gameable. Write a cheapest fix that "
                    + "reduces this rule, or withdraw the pair and list the rule as not-yet-covered.");
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    /// <summary>
    /// The findings. A pair marked <c>gate1-gameable</c> is one where the degenerate edit *does*
    /// pay: the rule stops charging and the total falls, so an agent is rewarded for an edit that
    /// improved nothing. Writing the gate as pass/fail only left nowhere to put that except a red
    /// build, and a red build is not a finding — it is a blocked branch that gets deleted or
    /// tuned until it passes, which is exactly the outcome the gate exists to prevent.
    ///
    /// <para>So the evidence stays executable and the claim is inverted: the drop is asserted. If
    /// someone repairs the rule, this fails and says to promote the pair, which is the one moment
    /// the repair could otherwise go unnoticed and the finding rot into a slander.</para>
    ///
    /// <para>The marker is a claim the fixture cannot check: that the cheapest fix is <b>degenerate</b>
    /// — satisfying the rule while leaving the design no better, and preferably worse. Some rules
    /// want surface deleted, and for those the honest cheapest fix is a real improvement whose score
    /// <i>should</i> fall; marking that pair gameable would be a false accusation dressed as data.
    /// Writing a degenerate fix is the author's job, and the note is where the argument for it goes.</para>
    /// </summary>
    [Fact]
    public async Task KnownGameableFixtures_StillLowerTheScore()
    {
        var failures = new List<string>();
        foreach (var pair in DiscoverPairs().Where(p => p.GameableNote is not null))
        {
            int before = (await ScoreAsync(pair.BeforeFile)).Total;
            int cheapest = (await ScoreAsync(pair.CheapestFixFile)).Total;
            if (cheapest < before) continue;

            failures.Add(
                $"{pair.Label}: marked gate1-gameable (\"{pair.GameableNote}\") but the cheapest fix "
                + $"scores {cheapest}, not below {before}. Either the rule was repaired — in which case "
                + "delete the marker and let CheapestFix_NeverLowersTheScore hold the line — or the "
                + "fixture drifted and the finding it records is no longer true.");
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
        var failures = new List<string>();
        foreach (var pair in DiscoverPairs())
        {
            var report = await ScoreAsync(pair.BeforeFile);
            var fired = report.ByRule.Where(kv => kv.Value != 0).Select(kv => kv.Key).ToList();

            foreach (var rule in pair.Rules)
                if (report.ByRule.GetValueOrDefault(rule) == 0)
                    failures.Add(
                        $"{pair.Label}: declares '{rule}' but that rule does not fire in {Path.GetFileName(pair.BeforeFile)}. "
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
        var failures = new List<string>();
        foreach (var pair in DiscoverPairs().Where(p => p.GoodFixFile is not null))
        {
            int before = (await ScoreAsync(pair.BeforeFile)).Total;
            int good = (await ScoreAsync(pair.GoodFixFile!)).Total;
            if (good >= before)
                failures.Add($"{pair.Label}: good fix scores {good}, not below {before}.");
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    /// <summary>
    /// A variant that does not compile scores nearly nothing and passes the gate by being empty.
    /// The shared-solution harness could not tell that apart from a fixture that legitimately
    /// scored nothing, because it never compiled a variant on its own. Isolation makes the question
    /// answerable, so it gets asked: every variant must compile by itself, which is also what
    /// enforces the fixtures' self-containment rule rather than leaving it to authoring discipline.
    /// </summary>
    [Fact]
    public async Task EveryVariant_CompilesOnItsOwn()
    {
        var failures = new List<string>();
        foreach (var pair in DiscoverPairs())
        {
            foreach (var file in new[] { pair.BeforeFile, pair.CheapestFixFile, pair.GoodFixFile }.Where(f => f is not null))
            {
                var health = (await ScoreAsync(file!)).BuildHealth;
                if (!health.Degraded) continue;

                var errors = health.Diagnostics
                    .Take(5)
                    .Select(d => $"      {d.Id} line {d.Line}: {d.Message}");
                failures.Add(
                    $"{Path.GetFileName(file!)}: {health.CompilationErrorCount} compilation error(s) when compiled alone."
                    + Environment.NewLine + string.Join(Environment.NewLine, errors) + Environment.NewLine
                    + "    A fixture must be self-contained — it is scored as a solution of one file, so a type "
                    + "it borrows from another fixture is not there.");
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    // ---------------- The ratchet ----------------

    [Fact]
    public void EveryScoredRule_IsCoveredOrDeclaredUncovered()
    {
        var scored = ScoredRules();
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
    /// a reason is a claim, so a stale exemption goes on excusing every future rule of the same
    /// shape. Five entries claiming "needs the isolated-variant harness" outlived their reason the
    /// moment that harness landed, and moving them was a manual step this test cannot force —
    /// which is exactly why the reasons have to be specific enough to notice.</para>
    /// </summary>
    [Fact]
    public void EveryRule_BelongsToExactlyOneBucket_AndEveryEntryStillDescribesSomethingTrue()
    {
        var scored = ScoredRules();
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

    /// <param name="GameableNote">
    /// Non-null when the Before file carries a <c>// gate1-gameable:</c> line: this pair records a
    /// rule that <i>fails</i> the gate, with the note saying why the cheapest fix is degenerate.
    /// </param>
    private sealed record FixturePair(
        string Label, string BeforeFile, string CheapestFixFile, string? GoodFixFile,
        IReadOnlyList<string> Rules, string? GameableNote);

    private static string SampleSolutionDirectory()
    {
        var testDir = Path.GetDirectoryName(typeof(GateOneFixtureTests).Assembly.Location)!;
        return Path.Combine(SampleSolutionFixture.FindRepoRoot(testDir), "test", "SampleSolution");
    }

    private static string RulesDirectory()
        => Path.Combine(SampleSolutionDirectory(), GateAssembly, "Rules");

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

            var gameable = ParseHeader(beforePath, GameableHeader);
            Assert.True(gameable is null or { Length: > 0 },
                $"Gate 1 fixture '{label}' has an empty '{GameableHeader}' marker. Recording a rule as gameable "
                + "without saying why the cheapest fix is degenerate is an accusation, not a finding.");

            pairs.Add(new FixturePair(label, beforePath, cheapestPath,
                File.Exists(goodPath) ? goodPath : null, rules, gameable));
        }
        return pairs;
    }

    private static List<string> ParseDeclaredRules(string path)
        => ParseHeader(path, GateHeader)
            ?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList()
           ?? new List<string>();

    /// <summary>
    /// The text after the first line beginning with <paramref name="header"/>, or null if there is
    /// none. Prefix-matched on the full header including its colon, so <c>// gate1-gameable:</c> is
    /// not read as a <c>// gate1:</c> line.
    /// </summary>
    private static string? ParseHeader(string path, string header)
    {
        foreach (var line in File.ReadLines(path))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith(header, StringComparison.Ordinal))
                return trimmed[header.Length..].Trim();
        }
        return null;
    }

    private static HashSet<string> ScoredRules()
        => SurfaceScoreConfig.Default().Weights
            .Where(kv => kv.Value != 0)
            .Select(kv => kv.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Rules declared by some discovered pair — including a pair that records the rule as gameable.
    /// "Covered" means the question has been asked and the answer written down, not that the answer
    /// was the good one; a rule with a finding against it is the opposite of unexamined, and putting
    /// it back in <see cref="NotYetCovered"/> would lose the finding and invite someone to
    /// rediscover it. Which answer a pair got is <see cref="CheapestFix_NeverLowersTheScore"/>'s and
    /// <see cref="KnownGameableFixtures_StillLowerTheScore"/>'s to report, not this method's.
    ///
    /// <para>A rule may have more than one pair, and <c>booleanParameter</c> does: the same edit is
    /// gated under one choice of identifier and gameable under another, so a single pair would have
    /// reported whichever answer its author happened to write. Set semantics are right here —
    /// coverage is per rule — but the verdict is per pair, which is why the two verdict tests read
    /// pairs and this one reads rules.</para>
    /// </summary>
    private static HashSet<string> CoveredRules()
    {
        var covered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in DiscoverPairs())
            foreach (var rule in pair.Rules)
                covered.Add(rule);
        return covered;
    }

    /// <summary>
    /// The variant's score, measured rather than reconstructed: the file is compiled as a solution
    /// of its own, so the report's total is the file's total and no filter stands between them.
    /// </summary>
    private static Task<ScoreReport> ScoreAsync(string fixtureFile)
        => IsolatedVariantScorer.ScoreAsync(fixtureFile, GateAssembly, SampleSolutionDirectory());
}
