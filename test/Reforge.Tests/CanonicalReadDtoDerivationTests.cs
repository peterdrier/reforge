namespace Reforge.Tests;

/// <summary>
/// Canonical read DTOs are derived from what each section exports from its contracts surface —
/// a <c>&lt;X&gt;.Contracts</c> assembly, or a <c>Contracts/</c> folder inside the section's own
/// assembly. There is no config field to override them.
/// </summary>
[Collection("SampleSolution")]
public class CanonicalReadDtoDerivationTests
{
    private readonly SampleSolutionFixture _fixture;
    public CanonicalReadDtoDerivationTests(SampleSolutionFixture fixture) => _fixture = fixture;

    private async Task<CanonicalReadDtoSet> DeriveAsync()
    {
        var cfg = SurfaceScoreConfig.Default();
        var classified = await SolutionClassifier.ClassifyAsync(
            _fixture.Solution, cfg, LocationHelper.GetSolutionDirectory(_fixture.Solution), CancellationToken.None);
        return CanonicalReadDtoSet.Derive(classified);
    }

    private static IEnumerable<string> Names(CanonicalReadDtoSet set, string section) =>
        set.ForSection(section).Select(c => c.Type.Name);

    [Fact]
    public async Task Derive_TakesPublicDtosFromTheContractsAssembly()
    {
        var canonical = await DeriveAsync();

        // Camp's read API lives in the sibling SampleSolution.Camp.Contracts assembly, which folds
        // into the Camp section.
        var camp = Names(canonical, "Camp").ToList();
        Assert.Contains("CampInfo", camp);
        Assert.Contains("CampSettingsInfo", camp);
        Assert.Contains("CampSummary", camp);
    }

    [Fact]
    public async Task Derive_TakesPublicDtosFromAContractsFolderInTheSectionsOwnAssembly()
    {
        var canonical = await DeriveAsync();

        // Lodge has no .Contracts assembly — only SampleSolution.Lodge/Contracts/.
        Assert.Contains("LodgeStayInfo", Names(canonical, "Lodge"));
    }

    [Fact]
    public async Task Derive_ExcludesInternalTypesDeclaredInAContractsLocation()
    {
        var canonical = await DeriveAsync();

        // LodgeSecretInfo sits in the same Contracts/ folder as LodgeStayInfo but is internal, so
        // no other section can name it. Location is not evidence of a published read API.
        Assert.DoesNotContain("LodgeSecretInfo", Names(canonical, "Lodge"));
    }

    [Fact]
    public async Task Derive_ExcludesTypesDeclaredOffTheContractsSurface()
    {
        var canonical = await DeriveAsync();

        // Public, exported, a plain data carrier — but declared in SampleSolution.Camp with no
        // Contracts/ folder above it, so Camp has not published it.
        Assert.DoesNotContain("CampLegacyEntity", Names(canonical, "Camp"));
        // Same for the Camp section's cache entry type.
        Assert.DoesNotContain("CampCacheEntry", Names(canonical, "Camp"));
    }

    [Fact]
    public async Task Derive_SectionWithNoContractsSurface_ContributesNothing()
    {
        var canonical = await DeriveAsync();

        // Tent and Dorm have neither a .Contracts assembly nor a Contracts/ folder. Config used to
        // be able to declare a read API for them anyway; now the absence of the boundary shows.
        Assert.Empty(canonical.ForSection("Tent"));
        Assert.Empty(canonical.ForSection("Dorm"));
        Assert.DoesNotContain("Tent", canonical.Sections);
        Assert.DoesNotContain("Dorm", canonical.Sections);
    }

    [Fact]
    public async Task Derive_MatchesOnSymbolIdentityNotSimpleName()
    {
        var canonical = await DeriveAsync();

        var camp = canonical.ForSection("Camp").Single(c => c.Type.Name == "CampInfo");
        Assert.True(canonical.Contains(camp.Type));

        // A same-named type from a section with no contracts surface must not ride along on the
        // name. DormInfo is exported but Dorm publishes nothing, so it is not canonical anywhere.
        var cfg = SurfaceScoreConfig.Default();
        var classified = await SolutionClassifier.ClassifyAsync(
            _fixture.Solution, cfg, LocationHelper.GetSolutionDirectory(_fixture.Solution), CancellationToken.None);
        var dormInfo = classified.Single(c => c.Type.Name == "DormInfo");
        Assert.False(canonical.Contains(dormInfo.Type));
    }

    [Fact]
    public async Task Derive_OrdersSectionDtosByAnchorPreference()
    {
        var canonical = await DeriveAsync();

        // *Info first, then shortest — so the section-shape analyzer's fallback picks CampInfo as
        // Camp's primary anchor rather than CampSeasonInfo or CampSummary.
        Assert.Equal("CampInfo", canonical.ForSection("Camp").First().Type.Name);
    }
}
