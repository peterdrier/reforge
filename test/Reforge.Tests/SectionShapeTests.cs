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
        var cfg = new SurfaceScoreConfig
        {
            Sections =
            {
                ["Camp"] = new SectionRule
                {
                    RepositoryInterfaces = { "ICampRepository" },
                    ServiceInterfaces = { "ICampSectionService" },
                    ReadServiceInterfaces = { "ICampServiceRead" },
                    EscapeHatchReadMethods =
                    {
                        new EscapeHatchReadMethod { Method = "ICampServiceRead.IsUserCampLeadAsync", Reason = "legacy", Since = "2026-02", Owner = "camps" }
                    }
                }
            }
        };
        cfg.BuildEffectiveSections();
        var dir = LocationHelper.GetSolutionDirectory(_fixture.Solution);
        var classified = (await SolutionClassifier.ClassifyAsync(_fixture.Solution, cfg, dir, CancellationToken.None)).ToList();
        var arch = await SectionShapeAnalyzer.AnalyzeAsync(_fixture.Solution, classified, cfg, dir, CancellationToken.None);
        var camp = arch.Sections.Single(s => s.Name == "Camp");

        Assert.NotNull(camp.PrimaryInfoDto);
        Assert.NotEmpty(camp.DerivableReadMethods);                       // advisory present
        Assert.Single(camp.EscapeHatches);                               // visible debt rendered
        Assert.Contains(camp.ChargedReadMethods, m => m.Method == "IsUserCampLeadAsync" && m.EscapeHatch);
    }
}
