using Microsoft.CodeAnalysis;

namespace Reforge.Tests;

/// <summary>
/// Plan B - section-architecture scored rules (crossSectionWriteSurface, missing*,
/// readSurfaceProjectionMethod) + conservationAnchors, exercised end-to-end through
/// SurfaceScoreEngine against the sample solution with an explicit Camp-section config.
/// </summary>
[Collection("SampleSolution")]
public class SectionArchitectureTests
{
    private readonly SampleSolutionFixture _fixture;
    public SectionArchitectureTests(SampleSolutionFixture fixture) => _fixture = fixture;

    private string Dir => LocationHelper.GetSolutionDirectory(_fixture.Solution);

    /// <summary>A config that maps the Camp fixtures into a repo-backed "Camp" section.</summary>
    private static SurfaceScoreConfig CampConfig()
    {
        var cfg = new SurfaceScoreConfig
        {
            Sections =
            {
                ["Camp"] = new SectionRule
                {
                    RepositoryInterfaces = { "ICampRepository" },
                    ServiceInterfaces = { "ICampSectionService" },
                    ReadServiceInterfaces = { "ICampServiceRead" }
                    // primaryInfoDto / settingsInfoDto left to convention -> CampInfo / CampSettingsInfo
                }
            }
        };
        cfg.BuildEffectiveSections();
        return cfg;
    }

    private async Task<ScoreReport> Score(SurfaceScoreConfig cfg)
    {
        var engine = new SurfaceScoreEngine(cfg, Dir);
        return await engine.ScoreAsync(_fixture.Solution, CancellationToken.None);
    }

    // ---------------- Task 1: weights + glossary + axis ----------------

    [Fact]
    public void Default_HasSectionArchitectureWeights()
    {
        var cfg = SurfaceScoreConfig.Default();
        Assert.Equal(15, cfg.Weight("crossSectionWriteSurface"));
        Assert.Equal(10, cfg.Weight("missingReadSurface"));
        Assert.Equal(10, cfg.Weight("missingWriteSurface"));
        Assert.Equal(10, cfg.Weight("missingPrimaryInfoDto"));
        Assert.Equal(4, cfg.Weight("readSurfaceProjectionMethod"));
    }

    [Fact]
    public void Glossary_HasFactualLinesForNewRules_OnSurfaceAxis()
    {
        foreach (var rule in new[]
        {
            "crossSectionWriteSurface", "missingReadSurface", "missingWriteSurface",
            "missingPrimaryInfoDto", "readSurfaceProjectionMethod"
        })
        {
            Assert.True(SurfaceScoreRuleGlossary.Descriptions.ContainsKey(rule), $"missing glossary: {rule}");
            Assert.False(SurfaceScoreRuleGroups.IsInternalComplexity(rule), $"should be surface axis: {rule}");
        }
    }
}
