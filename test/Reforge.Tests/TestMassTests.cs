namespace Reforge.Tests;

/// <summary>
/// The test-mass column (#37): size of each section's test corpus, attributed by project reference.
/// The sample solution carries three test projects on purpose — one referencing a single section,
/// one referencing two with a name that breaks the tie, and one referencing two with a name that
/// doesn't — because those are the three outcomes the attribution can produce.
/// </summary>
[Collection("SampleSolution")]
public class TestMassTests
{
    private readonly SampleSolutionFixture _fixture;
    public TestMassTests(SampleSolutionFixture fixture) => _fixture = fixture;

    private async Task<ScoreReport> Score()
    {
        var engine = new SurfaceScoreEngine(
            SurfaceScoreConfig.Default(), LocationHelper.GetSolutionDirectory(_fixture.Solution));
        return await engine.ScoreAsync(_fixture.Solution, CancellationToken.None);
    }

    [Fact]
    public async Task TestProject_IsAttributedByReference_NotByItsOwnName()
    {
        var report = await Score();

        // SampleSolution.Camp.Tests references SampleSolution.Camp and nothing else.
        var camp = report.TestsBySection["Camp"];
        Assert.Equal(1, camp.Projects);
        Assert.Equal(1, camp.Files);
        Assert.True(camp.Loc > 0);
        // Ratio against the section's own production LOC, which is what makes two sections
        // comparable — raw test LOC scales with section size.
        Assert.Equal(
            (int)Math.Round(camp.Loc * 100.0 / report.MetricsBySection["Camp"].LocProd),
            camp.LocVsProdPercent);
    }

    [Fact]
    public async Task AmbiguousReferences_AreBrokenByTheProjectName()
    {
        var report = await Score();

        // SampleSolution.Lodge.IntegrationTests references Lodge AND Camp. Its name names Lodge.
        Assert.Equal(1, report.TestsBySection["Lodge"].Projects);
        // ...and the Camp column does not also count it: attribution is one section per project.
        Assert.Equal(1, report.TestsBySection["Camp"].Projects);
    }

    [Fact]
    public async Task UnattributableProject_IsInTheRollupAndNoSection_WithADiagnostic()
    {
        var report = await Score();

        Assert.Contains("SampleSolution.Bridge.Tests", report.UnattributedTestProjects);
        Assert.Contains(report.Diagnostics,
            d => d.Code == "unattributedTestProject" && d.Message.Contains("SampleSolution.Bridge.Tests"));

        // The rollup counts all three; the sections account for two. An unattributed project is
        // reported as missing from the columns rather than assigned to a section it doesn't test.
        Assert.Equal(3, report.Tests.Projects);
        Assert.Equal(2, report.TestsBySection.Values.Sum(t => t.Projects));
        Assert.True(report.Tests.Loc > report.TestsBySection.Values.Sum(t => t.Loc));
    }

    [Fact]
    public async Task BuildIntermediates_AreNotTestCode()
    {
        var report = await Score();

        // Each sample test project has exactly one hand-written file. The SDK adds AssemblyInfo and
        // GlobalUsings under obj/, which a document walk sees and a symbol walk doesn't.
        Assert.Equal(3, report.Tests.Files);
    }

    [Fact]
    public async Task TestMass_ScoresNothing()
    {
        var report = await Score();

        Assert.True(report.Tests.Loc > 0);
        Assert.Equal(report.SurfaceTotal + report.InternalComplexityTotal, report.Total);
        Assert.DoesNotContain(report.ByRule.Keys, k => k.Contains("test", StringComparison.OrdinalIgnoreCase));
    }
}
