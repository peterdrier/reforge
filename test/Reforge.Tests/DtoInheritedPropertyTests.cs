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
}
