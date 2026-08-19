namespace Reforge.Tests;

/// <summary>
/// Issue #29 (3b): a DTO's published shape includes what it inherits, so the score has to as well.
/// Scoring only <c>GetMembers()</c> let an agent zero the charge by moving properties up to a base
/// class matching no DTO pattern, changing nothing a consumer can see.
/// </summary>
[Collection("SampleSolution")]
public class DtoInheritedPropertyTests
{
    private readonly SampleSolutionFixture _fixture;

    public DtoInheritedPropertyTests(SampleSolutionFixture fixture)
    {
        _fixture = fixture;
    }

    private async Task<ScoreReport> ScoreDefaultAsync()
    {
        var cfg = SurfaceScoreConfig.Default();
        var engine = new SurfaceScoreEngine(cfg, LocationHelper.GetSolutionDirectory(_fixture.Solution));
        return await engine.ScoreAsync(_fixture.Solution, CancellationToken.None);
    }

    private static IEnumerable<ScoreEntry> Entries(ScoreReport report) =>
        report.Groups.Values.SelectMany(g => g.Entries);

    [Fact]
    public async Task HoistingSomePropertiesToABase_StillChargesThem()
    {
        var report = await ScoreDefaultAsync();
        var scored = Entries(report)
            .Where(e => e.Symbol is "Id" or "Title" or "CreatedAt" or "Summary")
            .Where(e => e.File.Contains("InheritedDtoFixtures"))
            .ToList();

        // Three inherited + one declared, each charged once.
        Assert.Contains(scored, e => e.Symbol == "Summary" && e.Detail is not null && !e.Detail.Contains("inherited"));
        foreach (var inherited in new[] { "Id", "Title", "CreatedAt" })
            Assert.Contains(scored, e => e.Symbol == inherited && e.Detail is not null
                                         && e.Detail.Contains("inherited from ReportEnvelopeBase"));
    }

    [Fact]
    public async Task HoistingEveryPropertyToABase_StillScoresAsADto()
    {
        var report = await ScoreDefaultAsync();

        // The cheaper hole: with no properties of its own the derived type stopped looking like a
        // data carrier, so even publicDtoType vanished. Its shape is unchanged to a consumer.
        Assert.Contains(Entries(report), e =>
            e.Rule == "publicDtoType" && e.Symbol == "FullyHoistedReportInfo");

        var props = Entries(report)
            .Where(e => e.File.Contains("InheritedDtoFixtures"))
            .Where(e => e.Symbol is "Name" or "Sequence")
            .ToList();
        Assert.Equal(2, props.Count);
        Assert.All(props, e => Assert.Contains("inherited from FullyHoistedBase", e.Detail));
    }

    [Fact]
    public async Task InheritedBehaviour_DisqualifiesTheTypeAsADataCarrier()
    {
        var report = await ScoreDefaultAsync();

        // A base carrying a public method makes the derived type not a pure data carrier — a
        // consumer can call it, which is the same reason a declared method disqualifies a type.
        Assert.DoesNotContain(Entries(report), e =>
            e.Rule == "publicDtoType" && e.Symbol == "NotADataCarrierInfo");
    }

    [Fact]
    public async Task APropertyIsChargedOnceEvenWhenRedeclared()
    {
        var report = await ScoreDefaultAsync();

        // Every DTO property entry, keyed by the type it was charged against plus the property
        // name, must be unique — a derived declaration shadowing a base one must not pay twice.
        var dtoRules = new[] { "dtoScalarProperty", "dtoCollectionProperty", "dtoNestedProperty" };
        var keys = Entries(report)
            .Where(e => dtoRules.Contains(e.Rule))
            .Select(e => $"{e.File}|{e.Line}|{e.Symbol}|{e.Group}")
            .ToList();

        Assert.Equal(keys.Count, keys.Distinct().Count());
    }

    [Fact]
    public async Task AConstructedGenericBaseThatIsItselfADto_IsNotChargedTwice()
    {
        var report = await ScoreDefaultAsync();

        // GenericEnvelopeInfo<T> is a scored DTO in its own right. The derived type sees it as the
        // constructed GenericEnvelopeInfo<int>, whose display string differs from the declaration's —
        // so a key built from one and queried with the other misses, and the base's properties are
        // charged a second time against the derived type.
        var doubleCharged = Entries(report)
            .Where(e => e.File.Contains("InheritedDtoFixtures"))
            .Where(e => e.Symbol is "Id" or "Label")
            .Where(e => e.Detail is not null && e.Detail.Contains("inherited from GenericEnvelopeInfo"))
            .ToList();

        Assert.Empty(doubleCharged);
        // The base still pays for its own.
        Assert.Contains(Entries(report), e => e.Symbol == "Label" && e.Rule == "dtoScalarProperty");
    }

    [Fact]
    public async Task IndexerOverloads_AreEachCharged()
    {
        var report = await ScoreDefaultAsync();

        // Both of IndexedReportInfo's indexers are named `Item`, so a name-only de-duplication key
        // would charge only the first. The fixture file declares three published indexers in all —
        // two overloads on IndexedReportInfo plus one on IndexerOnlyReportInfo — and a name-only key
        // would collapse the pair and report two, so this count discriminates.
        var indexers = Entries(report)
            .Where(e => e.File.Contains("InheritedDtoFixtures"))
            .Where(e => e.Symbol == "this[]")
            .ToList();

        Assert.Equal(3, indexers.Count);
    }

    [Fact]
    public async Task InheritedEvent_DisqualifiesTheTypeAsADataCarrier()
    {
        var report = await ScoreDefaultAsync();

        // An event is behaviour a consumer can subscribe to. CanonicalReadDtoSet.IsDataCarrier has
        // always treated it so; this predicate did not, and once the walk climbs base types that
        // gap would admit a DTO-named class whose base publishes an event.
        Assert.DoesNotContain(Entries(report), e =>
            e.Rule == "publicDtoType" && e.Symbol == "EventingReportInfo");
    }

    [Fact]
    public async Task ADefaultInterfaceMethod_DisqualifiesTheTypeAsADataCarrier()
    {
        var report = await ScoreDefaultAsync();

        // Behaviour the type never declares anywhere, reachable through the interface.
        Assert.DoesNotContain(Entries(report), e =>
            e.Rule == "publicDtoType" && e.Symbol == "DefaultMethodReportInfo");
    }

    [Fact]
    public async Task AnExplicitInterfaceImplementation_DisqualifiesTheTypeAsADataCarrier()
    {
        var report = await ScoreDefaultAsync();

        // Private on the symbol, callable by anyone who casts. An accessibility filter skips it and
        // the interface scan skips it too (the interface declaration is abstract), so a reject-list
        // predicate misses it from both directions — which is why this one is an allowlist.
        Assert.DoesNotContain(Entries(report), e =>
            e.Rule == "publicDtoType" && e.Symbol == "ExplicitlyImplementedReportInfo");
    }

    [Fact]
    public async Task ADtoWhoseOnlyPropertiesAreIndexers_IsStillScored()
    {
        var report = await ScoreDefaultAsync();

        // CanonicalReadDtoSet.IsCarriedData excludes indexers on purpose — an indexer is not a
        // nameable fact and cannot become an inventory path. But ScoreDtoSurface charges indexers
        // as published properties, so a type with only indexers would otherwise be scored as
        // nothing at all: not a data carrier, and never reached.
        Assert.Contains(Entries(report), e =>
            e.Rule == "publicDtoType" && e.Symbol == "IndexerOnlyReportInfo");
    }

    [Fact]
    public async Task ABaseInAnotherProjectOfTheSolution_IsStillInsideTheBoundary()
    {
        var report = await ScoreDefaultAsync();

        // The walk stops at the solution boundary, and that boundary is assembly membership — the
        // definition SolutionClassifier itself uses — not whether the base symbol arrives with a
        // source location. A project referenced as a compiled DLL rather than a ProjectReference
        // yields a metadata symbol with no source location while being just as much part of the
        // solution, and testing IsInSource would silently stop the walk there.
        var inherited = Entries(report)
            .Where(e => e.Symbol is "Id" or "Origin")
            .Where(e => e.Detail is not null && e.Detail.Contains("inherited from CrossProjectEnvelopeBase"))
            .ToList();

        Assert.Equal(2, inherited.Count);
        // Charged to the publishing section, not the declaring one.
        Assert.All(inherited, e => Assert.Equal("Reporting", e.Group));
    }
}
