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
}
