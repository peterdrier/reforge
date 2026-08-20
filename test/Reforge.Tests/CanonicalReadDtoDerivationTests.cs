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
        return CanonicalReadDtoSet.Derive(classified, LocationHelper.GetSolutionDirectory(_fixture.Solution));
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
        Assert.DoesNotContain("CampLegacyStay", Names(canonical, "Camp"));
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
    public async Task Derive_ExcludesTypesThatInheritBehavior()
    {
        var canonical = await DeriveAsync();

        // LodgeOccupancyTally is exported, sits in Contracts/, and declares one property and no
        // methods — but it extends List<int>, so a consumer gets Add/Remove/Insert through it.
        // Counting only directly-declared members would publish a behavioral type as a read DTO.
        Assert.DoesNotContain("LodgeOccupancyTally", Names(canonical, "Lodge"));

        // Same idea one level subtler: LodgeArchiveRow's only method is an EXPLICIT interface
        // implementation, which is `private` on the class symbol but callable by anyone who casts
        // to ILodgeArchivable.
        Assert.DoesNotContain("LodgeArchiveRow", Names(canonical, "Lodge"));
    }

    [Fact]
    public async Task Derive_ExcludesBehaviorThatIsNotAnOrdinaryMethod()
    {
        var canonical = await DeriveAsync();
        var lodge = Names(canonical, "Lodge").ToList();

        // An event is a subscription surface and an operator is callable behavior; both live under
        // symbol shapes an "ordinary public method" check never sees.
        Assert.DoesNotContain("LodgeNotifyingRow", lodge);
        Assert.DoesNotContain("LodgeMoney", lodge);
    }

    [Fact]
    public async Task Derive_RequiresAnInstancePropertyToCarry()
    {
        var canonical = await DeriveAsync();

        // A static property is not a fact an anchor path can name. Admitting a type on one would
        // publish a canonical DTO whose inventory is empty — DtoInventory skips statics too, and
        // the two have to agree or the conservation gate anchors on nothing.
        Assert.DoesNotContain("LodgeStaticTotals", Names(canonical, "Lodge"));
    }

    [Fact]
    public async Task Derive_AdmitsTypesWhoseDataIsInherited()
    {
        var canonical = await DeriveAsync();

        // LodgeSeasonalRateInfo declares nothing at all; its one property comes from a data-only
        // base. It is still that data's carrier. (DtoInventoryTests pins the matching half — the
        // anchor inventory has to list the inherited property, not an empty path set.)
        Assert.Contains("LodgeSeasonalRateInfo", Names(canonical, "Lodge"));
    }

    [Fact]
    public async Task Derive_KeepsRecordsThatOnlyCarryData()
    {
        // Guard on the fix above: every record implements IEquatable<T>, so counting all interface
        // members rather than only non-abstract ones would disqualify every record in the solution.
        var cfg = SurfaceScoreConfig.Default();
        var classified = await SolutionClassifier.ClassifyAsync(
            _fixture.Solution, cfg, LocationHelper.GetSolutionDirectory(_fixture.Solution), CancellationToken.None);

        var record = classified.Single(c => c.Type.Name == "LodgeTariffRow");
        Assert.True(record.Type.IsRecord);
        Assert.True(CanonicalReadDtoSet.IsDataCarrier(
            record.Type, CanonicalReadDtoSet.AnalyzedAssemblies(classified)));
    }

    [Fact]
    public async Task Derive_ChecksEveryPartialDeclaration_NotJustThePrimaryLocation()
    {
        var cfg = SurfaceScoreConfig.Default();
        var classified = await SolutionClassifier.ClassifyAsync(
            _fixture.Solution, cfg, LocationHelper.GetSolutionDirectory(_fixture.Solution), CancellationToken.None);

        // Precondition: the PRIMARY location of this partial type is the half outside Contracts/,
        // so a check that reads only ClassifiedType.File would miss it. If syntax-tree ordering ever
        // flips this, the fixture stops exercising the multi-location path and must be re-pointed.
        var amenity = classified.Single(c => c.Type.Name == "LodgeAmenityInfo");
        Assert.DoesNotContain("/Contracts/", amenity.File.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);

        Assert.Contains("LodgeAmenityInfo", Names(CanonicalReadDtoSet.Derive(classified, LocationHelper.GetSolutionDirectory(_fixture.Solution)), "Lodge"));
    }

    [Fact]
    public async Task Derive_BreaksAnchorOrderTiesOnFullIdentity()
    {
        var cfg = SurfaceScoreConfig.Default();
        var dir = LocationHelper.GetSolutionDirectory(_fixture.Solution);
        var classified = (await SolutionClassifier.ClassifyAsync(
            _fixture.Solution, cfg, dir, CancellationToken.None)).ToList();

        // Two LodgeStayInfo types, same section, different namespaces — equal on both name and
        // length, so the comparator falls through to the type key. Deriving from the SAME set in
        // the opposite enumeration order has to produce the same order out: List.Sort is not
        // stable in general and is stable (insertion sort) for small inputs, so a comparator that
        // returned 0 here would silently hand back whichever came in first.
        var forward = Order(CanonicalReadDtoSet.Derive(classified, dir));
        var reversed = Order(CanonicalReadDtoSet.Derive(Enumerable.Reverse(classified), dir));

        Assert.Equal(new[] { "SampleSolution.Lodge.Contracts.LodgeStayInfo",
                             "SampleSolution.Lodge.Contracts.V2.LodgeStayInfo" }, forward);
        Assert.Equal(forward, reversed);

        static string[] Order(CanonicalReadDtoSet set) => set.ForSection("Lodge")
            .Where(c => c.Type.Name == "LodgeStayInfo")
            .Select(c => c.Type.ToDisplayString())
            .ToArray();
    }

    [Theory]
    // A solution checked out UNDER a directory named "Contracts". Every declaration's absolute path
    // contains that segment, so testing the raw path would put the whole solution on a contracts
    // surface — corrupting return credits, entity-leak exemptions and anchors solution-wide.
    [InlineData("/work/Contracts/Sln", "/work/Contracts/Sln/App.Lodge/Foo.cs", false)]
    [InlineData("/work/Contracts/Sln", "/work/Contracts/Sln/App.Lodge/Contracts/Foo.cs", true)]
    // And the same on Windows separators, which is what SyntaxTree.FilePath actually hands back here.
    [InlineData(@"C:\work\Contracts\Sln", @"C:\work\Contracts\Sln\App.Lodge\Foo.cs", false)]
    [InlineData(@"C:\work\Contracts\Sln", @"C:\work\Contracts\Sln\App.Lodge\Contracts\Foo.cs", true)]
    // A linked source in a SIBLING directory that merely shares the solution root's string prefix.
    // Stripping the prefix without a directory-boundary check would leave "Contracts/Foo.cs".
    [InlineData("/work/App", "/work/AppContracts/Foo.cs", false)]
    [InlineData(@"C:\work\App", @"C:\work\AppContracts\Foo.cs", false)]
    public void IsOnContractsSurface_ReadsSolutionRelativeSegmentsOnly(string solutionDir, string path, bool expected)
    {
        Assert.Equal(expected, CanonicalReadDtoSet.IsOnContractsSurface("App.Lodge", new[] { path }, solutionDir));
    }

    [Fact]
    public void IsOnContractsSurface_ContractsAssembly_NeedsNoPath()
    {
        Assert.True(CanonicalReadDtoSet.IsOnContractsSurface("App.Lodge.Contracts", new[] { "App.Lodge.Contracts/Foo.cs" }, "/sln"));
        Assert.False(CanonicalReadDtoSet.IsOnContractsSurface("App.Lodge", new[] { "App.Lodge/Foo.cs" }, "/sln"));
    }

    [Fact]
    public void UnreadConfigKeysWarning_NamesEveryDroppedKey()
    {
        // Shared by surface-score (as a `removed-config-field` diagnostic) and section-shape (as a
        // stderr warning) — both resolve DTO anchors the dropped `sections` policy used to feed.
        var cfg = SurfaceScoreConfig.Default();
        Assert.Null(cfg.UnreadConfigKeysWarning());

        cfg.Unrecognized = new() { ["zulu"] = default, ["alpha"] = default };

        var warning = cfg.UnreadConfigKeysWarning();
        Assert.NotNull(warning);
        Assert.Contains("alpha, zulu", warning);   // ordinal, so the message is stable run to run
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
