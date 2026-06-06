namespace Reforge.Tests;

[Collection("SampleSolution")]
public class CodeHealthAnalyzerTests
{
    private readonly SampleSolutionFixture _fixture;

    public CodeHealthAnalyzerTests(SampleSolutionFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task AnalyzeAsync_handles_partial_type_methods_declared_in_multiple_syntax_trees()
    {
        var reports = await CodeHealthAnalyzer.AnalyzeAsync(
            _fixture.Solution,
            "SampleSolution.Services",
            CancellationToken.None);

        Assert.Contains(reports, r => r.Name == "PartialHealthFixture");
    }

    [Fact]
    public async Task AnalyzeAsync_sums_lines_across_all_partial_declarations()
    {
        var reports = await CodeHealthAnalyzer.AnalyzeAsync(
            _fixture.Solution,
            "SampleSolution.Services",
            CancellationToken.None);

        var report = reports.Single(r => r.Name == "PartialHealthFixture");

        // Part1 declaration spans 11 lines, Part2 spans 6 -> 17 total. Before the fix,
        // Lines reflected only one partial declaration (whichever was deduped first).
        Assert.Equal(17, report.Lines);
    }
}
