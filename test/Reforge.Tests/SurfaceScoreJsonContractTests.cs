using System.Text.Json;
using Reforge.Commands;

namespace Reforge.Tests;

/// <summary>
/// The part of <c>surface-score --format json</c> an external daily driver parses. Names, nesting
/// and values of these fields are a contract: top level <c>total</c>, <c>surfaceTotal</c>,
/// <c>implementationShapeTotal</c>, <c>testTotal</c>, <c>byRule</c>, <c>edges</c> (always present,
/// an empty list included); per group <c>name</c>, <c>surfaceTotal</c>,
/// <c>implementationShapeTotal</c>, <c>total</c>, <c>surface</c>, <c>tests</c>. A change that
/// fails here breaks that consumer; anything else in the payload is free to change.
/// </summary>
[Collection("SampleSolution")]
public class SurfaceScoreJsonContractTests
{
    private readonly SampleSolutionFixture _fixture;
    public SurfaceScoreJsonContractTests(SampleSolutionFixture fixture) => _fixture = fixture;

    private static readonly string[] TopLevel = ["total", "surfaceTotal", "implementationShapeTotal", "testTotal", "byRule", "edges"];
    private static readonly string[] PerGroup = ["name", "surfaceTotal", "implementationShapeTotal", "total", "surface", "tests"];

    /// <summary>Every flag combination that changes what the JSON carries.</summary>
    public static TheoryData<bool, bool> Flags => new() { { false, false }, { true, false }, { false, true }, { true, true } };

    private static ScoreReport HandBuilt(bool withEdge = true)
    {
        var report = new ScoreReport { Total = 142, SurfaceTotal = 100, ImplementationShapeTotal = 42, TestTotal = 7 };
        report.ByRule["methodComplexity"] = 30;
        report.ByRule["exportedReadMethod"] = 12;
        report.ByRule["exportedWriteMethod"] = 88;
        report.ByRule["methodCount"] = 12;
        if (withEdge) report.Edges.Add(new SectionEdge("Web", "Camp", 3));

        var camp = new GroupScore
        {
            Name = "Camp", Total = 142, SurfaceTotal = 100, ImplementationShapeTotal = 42, MainSurfaceTotal = 100,
            Surface = new SurfaceCensus(4, 2, 9, 3, 5, 1, 2, 6, 1)
        };
        foreach (var (rule, points) in report.ByRule) camp.ByRule[rule] = points;
        report.Groups["Camp"] = camp;
        report.ConfiguredSections.Add("Camp");
        report.MetricsBySection["Camp"] = new SectionMetrics(1000, 10, 8, 2, 40,
            MetricDistribution.Empty, MetricDistribution.Empty, 300, "CampService");
        report.TestsBySection["Camp"] = new TestMass(500, 5, 1, 50, 12);
        report.TestScoreBySection["Camp"] = 7;
        return report;
    }

    private static JsonDocument Json(ScoreReport report, string? group = null, bool explain = false, bool helpers = false) =>
        JsonDocument.Parse(Capture(() => SurfaceScoreCommand.WriteJson(report, group, 10, 25, null, explain, helpers)));

    /// <summary>Compact re-serialization: property order and number text as emitted, whitespace aside.</summary>
    private static string Raw(JsonElement e) => JsonSerializer.Serialize(e);

    [Theory]
    [MemberData(nameof(Flags))]
    public void PinnedFields_KeepTheirNamesNestingAndValues(bool explain, bool helpers)
    {
        using var doc = Json(HandBuilt(), explain: explain, helpers: helpers);
        var root = doc.RootElement;

        Assert.Equal("142", Raw(root.GetProperty("total")));
        Assert.Equal("100", Raw(root.GetProperty("surfaceTotal")));
        Assert.Equal("42", Raw(root.GetProperty("implementationShapeTotal")));
        Assert.Equal("7", Raw(root.GetProperty("testTotal")));
        Assert.Equal("""{"exportedWriteMethod":88,"methodComplexity":30,"exportedReadMethod":12,"methodCount":12}""",
            Raw(root.GetProperty("byRule")));
        Assert.Equal("""[{"from":"Web","to":"Camp","count":3}]""", Raw(root.GetProperty("edges")));

        var group = Assert.Single(root.GetProperty("groups").EnumerateArray());
        Assert.Equal("\"Camp\"", Raw(group.GetProperty("name")));
        Assert.Equal("100", Raw(group.GetProperty("surfaceTotal")));
        Assert.Equal("42", Raw(group.GetProperty("implementationShapeTotal")));
        Assert.Equal("142", Raw(group.GetProperty("total")));
        Assert.Equal(
            """{"exported":{"types":4,"read":9,"write":3,"typesUnusedExternally":2,"methodsUnusedExternally":6,"writeUnusedExternally":1},"internal":{"types":2,"read":5,"write":1}}""",
            Raw(group.GetProperty("surface")));
        Assert.Equal(
            """{"score":7,"loc":500,"files":5,"projects":1,"locVsProdPercent":50,"methods":12,"methodsPerClass":1.5}""",
            Raw(group.GetProperty("tests")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Camp")]
    [InlineData("NoSuchGroup")]
    public void Edges_IsAlwaysPresent_EvenEmpty(string? group)
    {
        using var doc = Json(HandBuilt(withEdge: false), group);
        var edges = doc.RootElement.GetProperty("edges");
        Assert.Equal(JsonValueKind.Array, edges.ValueKind);
        Assert.Equal(0, edges.GetArrayLength());
    }

    /// <summary>The same fields, typed and matching the report, on a real scored solution.</summary>
    [Theory]
    [MemberData(nameof(Flags))]
    public async Task ScoredSolution_CarriesEveryPinnedField(bool explain, bool helpers)
    {
        var engine = new SurfaceScoreEngine(SurfaceScoreConfig.Default(), LocationHelper.GetSolutionDirectory(_fixture.Solution));
        var report = await engine.ScoreAsync(_fixture.Solution, CancellationToken.None);
        using var doc = Json(report, explain: explain, helpers: helpers);
        var root = doc.RootElement;

        foreach (var key in TopLevel) Assert.True(root.TryGetProperty(key, out _), $"top-level '{key}' missing");
        Assert.Equal(report.Total, root.GetProperty("total").GetInt32());
        Assert.Equal(report.SurfaceTotal, root.GetProperty("surfaceTotal").GetInt32());
        Assert.Equal(report.ImplementationShapeTotal, root.GetProperty("implementationShapeTotal").GetInt32());
        Assert.Equal(report.TestTotal, root.GetProperty("testTotal").GetInt32());
        Assert.Equal(report.ByRule.Keys.Order(StringComparer.Ordinal),
            root.GetProperty("byRule").EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.All(root.GetProperty("byRule").EnumerateObject(), p => Assert.Equal(report.ByRule[p.Name], p.Value.GetInt32()));
        Assert.Equal(report.Edges.Count, root.GetProperty("edges").GetArrayLength());

        var groups = root.GetProperty("groups").EnumerateArray().ToList();
        Assert.Equal(report.Groups.Count, groups.Count);
        foreach (var g in groups)
        {
            foreach (var key in PerGroup) Assert.True(g.TryGetProperty(key, out _), $"group '{key}' missing");
            var scored = report.Groups[g.GetProperty("name").GetString()!];
            Assert.Equal(scored.SurfaceTotal, g.GetProperty("surfaceTotal").GetInt32());
            Assert.Equal(scored.ImplementationShapeTotal, g.GetProperty("implementationShapeTotal").GetInt32());
            Assert.Equal(scored.Total, g.GetProperty("total").GetInt32());
            Assert.Equal(scored.Surface.ExportedTypes, g.GetProperty("surface").GetProperty("exported").GetProperty("types").GetInt32());
            Assert.Equal(report.TestValueOf(scored.Name).Score, g.GetProperty("tests").GetProperty("score").GetInt32());
        }
    }

    // ---------------- Fields outside the contract: opt-in or left out when empty ----------------

    [Fact]
    public void Default_LeavesOutTheGlossaryHelpersAndEmptyLists()
    {
        using var doc = Json(HandBuilt());
        var root = doc.RootElement;
        foreach (var key in new[] { "combinedTotal", "ruleGlossary", "helperCandidates", "duplicateOwners",
                     "suspiciousImprovements", "diagnostics", "conservationEvidence" })
            Assert.False(root.TryGetProperty(key, out _), $"'{key}' should be left out");
    }

    /// <summary>
    /// A missing conservationAnchors key marks a baseline older than the gate, so a fresh run writes
    /// the key even with nothing in it, and reads back as a baseline without the warning.
    /// </summary>
    [Fact]
    public void ConservationAnchors_AlwaysPresent_AndRoundTripsAsBaseline()
    {
        var report = HandBuilt();
        var json = Capture(() => SurfaceScoreCommand.WriteJson(report, null, 10, 25, null));
        using (var doc = JsonDocument.Parse(json))
        {
            var anchors = doc.RootElement.GetProperty("conservationAnchors");
            Assert.Equal(JsonValueKind.Array, anchors.ValueKind);
            Assert.Equal(0, anchors.GetArrayLength());
        }

        var path = Path.GetTempFileName();
        File.WriteAllText(path, json);
        try
        {
            var cmp = SurfaceScoreBaseline.Compare(HandBuilt(), path);
            Assert.False(cmp.BaselineAnchorsMissing);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Explain_AddsTheGlossary_HelpersAddsTheCandidates()
    {
        using var explained = Json(HandBuilt(), explain: true);
        Assert.True(explained.RootElement.GetProperty("ruleGlossary").TryGetProperty("methodComplexity", out _));

        var report = HandBuilt();
        report.HelperCandidates.Add(new HelperCandidate("Camp.Helpers", ["Build"]));
        using var withHelpers = Json(report, helpers: true);
        Assert.Equal("Camp.Helpers", withHelpers.RootElement.GetProperty("helperCandidates")[0].GetProperty("display").GetString());
    }

    [Fact]
    public void NonEmptyLists_ArePrinted()
    {
        var report = HandBuilt();
        report.Diagnostics.Add(new ScoreDiagnostic("info", "x", "y"));
        report.DuplicateOwners.Add("Camps: A, B");
        using var doc = Json(report);
        Assert.Equal(1, doc.RootElement.GetProperty("diagnostics").GetArrayLength());
        Assert.Equal(1, doc.RootElement.GetProperty("duplicateOwners").GetArrayLength());
    }

    private static string Capture(Action action)
    {
        var original = Console.Out;
        var sw = new StringWriter();
        Console.SetOut(sw);
        try { action(); }
        finally { Console.SetOut(original); }
        return sw.ToString();
    }
}
