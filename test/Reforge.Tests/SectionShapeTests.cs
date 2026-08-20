using Microsoft.CodeAnalysis;

namespace Reforge.Tests;

[Collection("SampleSolution")]
public class SectionShapeTests
{
    private readonly SampleSolutionFixture _fixture;
    public SectionShapeTests(SampleSolutionFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task SectionShape_RendersCampShapeAndAdvisory()
    {
        // Drive the analyzer directly (command IO is covered by a CLI smoke test in dogfooding).
        // Section membership needs no config at all.
        var cfg = SurfaceScoreConfig.Default();

        var dir = LocationHelper.GetSolutionDirectory(_fixture.Solution);
        var classified = (await SolutionClassifier.ClassifyAsync(_fixture.Solution, cfg, dir, CancellationToken.None)).ToList();
        var arch = await SectionShapeAnalyzer.AnalyzeAsync(_fixture.Solution, classified, cfg, dir, CancellationToken.None);
        var camp = arch.Sections.Single(s => s.Name == "Camp");

        Assert.NotNull(camp.PrimaryInfoDto);
        Assert.NotEmpty(camp.DerivableReadMethods);                       // advisory present
        Assert.Contains(camp.ChargedReadMethods, m => m.Method == "IsUserCampLeadAsync");
    }
}
