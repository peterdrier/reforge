using Microsoft.CodeAnalysis;

namespace Reforge.Tests;

[Collection("SampleSolution")]
public class SectionShapeAnalyzerTests
{
    private readonly SampleSolutionFixture _fixture;
    public SectionShapeAnalyzerTests(SampleSolutionFixture fixture) => _fixture = fixture;

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
                    // primaryInfoDto/settingsInfoDto left to convention -> CampInfo / CampSettingsInfo
                }
            }
        };
        cfg.BuildEffectiveSections();
        return cfg;
    }

    [Fact]
    public async Task Analyze_ResolvesCampShape()
    {
        var cfg = CampConfig();
        var dir = LocationHelper.GetSolutionDirectory(_fixture.Solution);
        var classified = (await SolutionClassifier.ClassifyAsync(_fixture.Solution, cfg, dir, CancellationToken.None)).ToList();
        var arch = await SectionShapeAnalyzer.AnalyzeAsync(_fixture.Solution, classified, cfg, dir, CancellationToken.None);
        var camp = arch.Sections.Single(s => s.Name == "Camp");

        Assert.True(camp.Facts.RepoBacked);
        Assert.Contains("ICampRepository", camp.OwnedRepositoryInterfaces);
        Assert.Contains("ICampServiceRead", camp.ReadServiceInterfaces);
        Assert.Contains("ICampSectionService", camp.FullServiceInterfaces);
        Assert.Equal("CampInfo", camp.PrimaryInfoDto!.Display.Split('.').Last());
        Assert.Equal("CampSettingsInfo", camp.SettingsInfoDto!.Display.Split('.').Last());
        // (cache resolution is covered by the cache-inference / default-primary tests below)
        // recursive path inventory present on the primary anchor
        Assert.Contains(camp.PrimaryInfoDto.Paths, p => p == "CampInfo.Seasons[].Members[].UserId");
        // charged read methods: predicate + projection; healthy: GetById (primary), GetSettings (settings)
        Assert.Contains(camp.ChargedReadMethods, m => m.Method == "IsUserCampLeadAsync" && m.Kind == ReadMethodKind.Predicate);
        Assert.Contains(camp.ChargedReadMethods, m => m.Method == "GetCampSummariesForYearAsync" && m.Kind == ReadMethodKind.ProjectionSummary);
        Assert.DoesNotContain(camp.ChargedReadMethods, m => m.Method == "GetByIdAsync");
        Assert.DoesNotContain(camp.ChargedReadMethods, m => m.Method == "GetSettingsAsync");
        // no missing surfaces: Camp has read+write+CampInfo
        Assert.Empty(camp.Missing);
    }

    [Fact]
    public async Task Analyze_AggregatesDtoAndInterfaceAnchors()
    {
        var cfg = CampConfig();
        var dir = LocationHelper.GetSolutionDirectory(_fixture.Solution);
        var classified = (await SolutionClassifier.ClassifyAsync(_fixture.Solution, cfg, dir, CancellationToken.None)).ToList();
        var arch = await SectionShapeAnalyzer.AnalyzeAsync(_fixture.Solution, classified, cfg, dir, CancellationToken.None);

        Assert.Contains(arch.DtoAnchors, a => a.Display.EndsWith("CampInfo") && a.Role == "primaryInfoDto");
        var read = arch.InterfaceAnchors.Single(a => a.Display.EndsWith("ICampServiceRead") && a.Role == "readServiceInterface");
        Assert.Contains(read.Methods, m => m.Name == "GetByIdAsync");
        Assert.Contains(arch.InterfaceAnchors, a => a.Display.EndsWith("ICampSectionService") && a.Role == "fullServiceInterface");
    }

    [Fact]
    public async Task Analyze_InfersCacheDtoFromCachingDecorator()
    {
        var cfg = CampConfig();   // cacheDto NOT configured
        var dir = LocationHelper.GetSolutionDirectory(_fixture.Solution);
        var classified = (await SolutionClassifier.ClassifyAsync(_fixture.Solution, cfg, dir, CancellationToken.None)).ToList();
        var arch = await SectionShapeAnalyzer.AnalyzeAsync(_fixture.Solution, classified, cfg, dir, CancellationToken.None);
        var camp = arch.Sections.Single(s => s.Name == "Camp");

        Assert.Equal("CampCacheEntry", camp.CacheDto!.Display.Split('.').Last());
        Assert.StartsWith("inferred:", camp.CacheDtoProvenance);
    }

    [Fact]
    public async Task Analyze_DefaultsCacheToPrimary_WhenNoDecorator()
    {
        // IDormServiceRead has no caching decorator -> cache falls back to the primary DormInfo.
        var cfg = new SurfaceScoreConfig
        {
            Sections = { ["Dorm"] = new SectionRule { RepositoryInterfaces = { "IDormRepository" }, ReadServiceInterfaces = { "IDormServiceRead" } } }
        };
        cfg.BuildEffectiveSections();
        var dir = LocationHelper.GetSolutionDirectory(_fixture.Solution);
        var classified = (await SolutionClassifier.ClassifyAsync(_fixture.Solution, cfg, dir, CancellationToken.None)).ToList();
        var arch = await SectionShapeAnalyzer.AnalyzeAsync(_fixture.Solution, classified, cfg, dir, CancellationToken.None);
        var dorm = arch.Sections.Single(s => s.Name == "Dorm");

        Assert.Equal("DormInfo", dorm.CacheDto!.Display.Split('.').Last());
        Assert.Equal("default-primary", dorm.CacheDtoProvenance);
    }
}
