using Microsoft.CodeAnalysis;

namespace Reforge.Tests;

/// <summary>
/// Sections are the sample solution's assemblies: Camp (+ Camp.Contracts folded in), Lodge, Dorm,
/// Tent, Reporting. No config maps types into them — these tests assert the shapes that fall out
/// of the assembly graph, plus the policy overrides that still come from config.
/// </summary>
[Collection("SampleSolution")]
public class SectionShapeAnalyzerTests
{
    private readonly SampleSolutionFixture _fixture;
    public SectionShapeAnalyzerTests(SampleSolutionFixture fixture) => _fixture = fixture;

    private async Task<SectionArchitecture> AnalyzeAsync(SurfaceScoreConfig? cfg = null)
    {
        cfg ??= SurfaceScoreConfig.Default();
        var dir = LocationHelper.GetSolutionDirectory(_fixture.Solution);
        var classified = (await SolutionClassifier.ClassifyAsync(_fixture.Solution, cfg, dir, CancellationToken.None)).ToList();
        return await SectionShapeAnalyzer.AnalyzeAsync(_fixture.Solution, classified, cfg, dir, CancellationToken.None);
    }

    [Fact]
    public async Task Analyze_ResolvesCampShape_FromAssembliesAlone()
    {
        var arch = await AnalyzeAsync();
        var camp = arch.Sections.Single(s => s.Name == "Camp");

        Assert.True(camp.Facts.RepoBacked);
        Assert.Contains("ICampRepository", camp.OwnedRepositoryInterfaces);
        // Declared in SampleSolution.Camp.Contracts — folded into this section.
        Assert.Contains(camp.ReadServiceInterfaces, i => i.Name == "ICampServiceRead");
        Assert.Contains(camp.FullServiceInterfaces, i => i.Name == "ICampSectionService");
        Assert.Equal("CampInfo", camp.PrimaryInfoDto!.Display.Split('.').Last());
        Assert.Equal("CampSettingsInfo", camp.SettingsInfoDto!.Display.Split('.').Last());
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
    public async Task Analyze_ShapesEverySection_NotJustConfiguredOnes()
    {
        var arch = await AnalyzeAsync();

        foreach (var expected in new[] { "Camp", "Core", "Dorm", "Lodge", "Reporting", "Services", "Tent", "Web" })
            Assert.Contains(arch.Sections, s => s.Name == expected);
        Assert.DoesNotContain(arch.Sections, s => s.Name.Contains("Contracts", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Analyze_AggregatesDtoAndInterfaceAnchors()
    {
        var arch = await AnalyzeAsync();

        Assert.Contains(arch.DtoAnchors, a => a.Display.EndsWith("CampInfo") && a.Role == "primaryInfoDto");
        var read = arch.InterfaceAnchors.Single(a => a.Display.EndsWith("ICampServiceRead") && a.Role == "readServiceInterface");
        Assert.Equal("Camp", read.Section);
        Assert.Contains(read.Methods, m => m.Name == "GetByIdAsync");
        Assert.Contains(arch.InterfaceAnchors, a => a.Display.EndsWith("ICampSectionService") && a.Role == "fullServiceInterface");
    }

    [Fact]
    public async Task Analyze_InfersCacheDtoFromCachingDecorator()
    {
        var arch = await AnalyzeAsync();       // cacheDto NOT configured
        var camp = arch.Sections.Single(s => s.Name == "Camp");

        Assert.Equal("CampCacheEntry", camp.CacheDto!.Display.Split('.').Last());
        Assert.StartsWith("inferred:", camp.CacheDtoProvenance);
    }

    [Fact]
    public async Task Analyze_DefaultsCacheToPrimary_WhenNoDecorator()
    {
        // IDormServiceRead has no caching decorator -> cache falls back to the primary DormInfo.
        var arch = await AnalyzeAsync();
        var dorm = arch.Sections.Single(s => s.Name == "Dorm");

        Assert.Equal("DormInfo", dorm.CacheDto!.Display.Split('.').Last());
        Assert.Equal("default-primary", dorm.CacheDtoProvenance);
    }

    [Fact]
    public async Task Analyze_FallsBackToDerivedCanonicalDto_WhenTheConventionMisses()
    {
        // Lodge publishes LodgeStayInfo from SampleSolution.Lodge/Contracts/ — no "LodgeInfo"
        // exists, so the <Section>Info convention misses and the derived set is the only thing
        // that can resolve the anchor. No config involved.
        var arch = await AnalyzeAsync();
        var lodge = arch.Sections.Single(s => s.Name == "Lodge");

        Assert.Equal("LodgeStayInfo", lodge.PrimaryInfoDto!.Display.Split('.').Last());
        Assert.DoesNotContain(lodge.Missing, m => m.Rule == "missingPrimaryInfoDto");
    }

    [Fact]
    public async Task Analyze_NoContractsSurface_LeavesThePrimaryAnchorUnresolved()
    {
        // Tent has neither a .Contracts assembly nor a Contracts/ folder, so it declares no read
        // API and nothing can invent one for it. Config used to be able to.
        var arch = await AnalyzeAsync();
        var tent = arch.Sections.Single(s => s.Name == "Tent");

        Assert.Null(tent.PrimaryInfoDto);
        Assert.Contains(tent.Missing, m => m.Rule == "missingPrimaryInfoDto");
    }

    [Fact]
    public async Task Analyze_PolicyPrimaryInfoDto_OverridesConvention()
    {
        // A section whose canonical DTO isn't "<Section>Info" still resolves through policy —
        // the one thing the assembly graph genuinely can't state.
        var cfg = SurfaceScoreConfig.Default();
        cfg.Sections["Tent"] = new SectionRule { PrimaryInfoDto = "DormInfo" };

        var arch = await AnalyzeAsync(cfg);
        var tent = arch.Sections.Single(s => s.Name == "Tent");

        Assert.Equal("DormInfo", tent.PrimaryInfoDto!.Display.Split('.').Last());
        Assert.DoesNotContain(tent.Missing, m => m.Rule == "missingPrimaryInfoDto");
    }
}
