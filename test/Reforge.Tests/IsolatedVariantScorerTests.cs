namespace Reforge.Tests;

/// <summary>
/// The Gate 1 harness's own tests. <see cref="GateOneFixtureTests"/> asks whether the rules are
/// gameable; this asks whether the thing asking is capable of noticing, which is a different
/// question and the one that has been wrong twice.
///
/// <para>The capability under test here is multi-section variants. Sections are derived from
/// assembly names, so a variant compiled as a single project has exactly one section and can never
/// fire a cross-section rule — not because those rules are fine, but because the harness cannot
/// construct the situation. Left that way, <c>crossSectionRepository</c>,
/// <c>crossSectionReadInterface</c>, <c>crossSectionFullService</c> and
/// <c>crossSectionWriteSurface</c> would sit in not-yet-covered with no path to ever leaving it,
/// and the backlog would be uncompletable by construction rather than by effort.</para>
///
/// <para>Uses a probe pair rather than a real fixture on purpose. A fixture declares a rule gated,
/// and proving the harness <i>can</i> fire a rule is not the same claim as proving that rule
/// survives its cheapest fix.</para>
/// </summary>
public class IsolatedVariantScorerTests
{
    private const string GateAssembly = "SampleSolution.Gate";
    private const string Probe = "_HarnessProbe.TwoSection.cs";

    private static string SampleSolutionDirectory()
    {
        var testDir = Path.GetDirectoryName(typeof(IsolatedVariantScorerTests).Assembly.Location)!;
        return Path.Combine(SampleSolutionFixture.FindRepoRoot(testDir), "test", "SampleSolution");
    }

    private static string ProbePath()
        => Path.Combine(SampleSolutionDirectory(), GateAssembly, "Rules", Probe);

    private static Task<ScoreReport> ScoreProbeAsync()
        => IsolatedVariantScorer.ScoreAsync(ProbePath(), GateAssembly, SampleSolutionDirectory());

    [Fact]
    public void SatellitesOf_TakesOnlyBareSectionSegments()
    {
        var satellites = IsolatedVariantScorer.SatellitesOf(ProbePath());

        Assert.Equal(new[] { "Camp" }, satellites.Keys);
        Assert.EndsWith("_HarnessProbe.TwoSection.Camp.cs", satellites["Camp"]);
    }

    /// <summary>
    /// A satellite is a section, not just another file: the probe's two types must land in two
    /// groups. If they collapsed into one the cross-section assertion below would still be
    /// checkable but would be testing nothing, so the grouping is asserted on its own.
    /// </summary>
    [Fact]
    public async Task SatelliteFile_BecomesItsOwnSection()
    {
        var report = await ScoreProbeAsync();

        Assert.False(report.BuildHealth.Degraded,
            $"probe did not compile: {string.Join("; ", report.BuildHealth.Diagnostics.Take(3).Select(d => d.Message))}");
        Assert.Contains("Gate", report.Groups.Keys);
        Assert.Contains("Camp", report.Groups.Keys);
    }

    /// <summary>
    /// The point of the whole mechanism: a rule that only fires across a section boundary fires.
    /// Asserted against the consumer's group as well as the total, because a cross-section rule
    /// charges the <i>consumer</i> — charging the dependency's section would invert who is being
    /// told to change, and the totals alone cannot tell those apart.
    /// </summary>
    [Fact]
    public async Task CrossSectionRule_FiresBetweenAVariantAndItsSatellite()
    {
        var report = await ScoreProbeAsync();

        Assert.Equal(SurfaceScoreConfig.Default().Weight("crossSectionFullService"),
            report.ByRule.GetValueOrDefault("crossSectionFullService"));
        Assert.Equal(SurfaceScoreConfig.Default().Weight("crossSectionFullService"),
            report.Groups["Gate"].ByRule.GetValueOrDefault("crossSectionFullService"));
    }
}
