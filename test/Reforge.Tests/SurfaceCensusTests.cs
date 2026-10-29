using System.Text.Json;
using Reforge.Commands;

namespace Reforge.Tests;

/// <summary>
/// The exported/internal x read/write census reported beside the score, and the
/// <c>snapshot --by-assembly</c> rows built from it. Informational: nothing here moves a point.
/// </summary>
[Collection("SampleSolution")]
public class SurfaceCensusTests
{
    private readonly SampleSolutionFixture _fixture;
    public SurfaceCensusTests(SampleSolutionFixture fixture) => _fixture = fixture;

    private async Task<ScoreReport> ScoreAsync()
    {
        var engine = new SurfaceScoreEngine(SurfaceScoreConfig.Default(), LocationHelper.GetSolutionDirectory(_fixture.Solution));
        return await engine.ScoreAsync(_fixture.Solution, CancellationToken.None);
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

    [Fact]
    public async Task EverySectionGetsACensus_AndGroupsCarryTheirSections()
    {
        var report = await ScoreAsync();

        Assert.All(report.ConfiguredSections, s => Assert.True(report.SurfaceBySection.ContainsKey(s), $"section '{s}' has no census"));
        foreach (var g in report.Groups.Values)
            Assert.Same(report.SurfaceBySection[g.Name], g.Surface);
    }

    [Fact]
    public async Task SolutionRollup_IsTheSumOfTheSections()
    {
        var report = await ScoreAsync();
        var sections = report.SurfaceBySection.Values.ToList();

        Assert.Equal(sections.Sum(s => s.ExportedTypes), report.Surface.ExportedTypes);
        Assert.Equal(sections.Sum(s => s.InternalTypes), report.Surface.InternalTypes);
        Assert.Equal(sections.Sum(s => s.ExportedWriteMethods), report.Surface.ExportedWriteMethods);
        Assert.Equal(sections.Sum(s => s.ExportedReadMethods), report.Surface.ExportedReadMethods);
        Assert.Equal(sections.Sum(s => s.ExportedMethodsUnusedExternally), report.Surface.ExportedMethodsUnusedExternally);
        // The corpus is the classified corpus: every type is exported or internal, nothing else.
        Assert.Equal(report.TypesAnalyzed, report.Surface.ExportedTypes + report.Surface.InternalTypes);
    }

    [Fact]
    public async Task Census_SplitsReadFromWrite_AndExportedFromInternal()
    {
        var report = await ScoreAsync();

        // Services holds the sample's application services: read and write methods on both sides.
        var services = report.SurfaceBySection["Services"];
        Assert.True(services.ExportedReadMethods > 0);
        Assert.True(services.ExportedWriteMethods > 0);
        // Reporting declares internal types (InternalCtorInput and friends) with methods of their own.
        var reporting = report.SurfaceBySection["Reporting"];
        Assert.True(reporting.InternalTypes > 0);
        Assert.True(reporting.InternalMethods > 0);
        // Unused-externally is a subset, never the whole or more.
        Assert.All(report.SurfaceBySection.Values, s =>
        {
            Assert.InRange(s.ExportedTypesUnusedExternally, 0, s.ExportedTypes);
            Assert.InRange(s.ExportedMethodsUnusedExternally, 0, s.ExportedMethods);
            Assert.InRange(s.ExportedWriteMethodsUnusedExternally, 0, Math.Min(s.ExportedWriteMethods, s.ExportedMethodsUnusedExternally));
        });
    }

    [Fact]
    public async Task UnusedExternally_SeesCrossAssemblyUse()
    {
        var report = await ScoreAsync();

        // Reporting (CampReportBuilder, CampDelegator) injects Camp's interfaces and Services calls
        // into Camp too, so Camp has exported surface that something else names.
        var camp = report.SurfaceBySection["Camp"];
        Assert.True(camp.ExportedTypesUnusedExternally < camp.ExportedTypes, "no Camp type is seen as used externally");
        Assert.True(camp.ExportedMethodsUnusedExternally < camp.ExportedMethods, "no Camp method is seen as used externally");

        // And the same walk records the edge that use creates.
        Assert.Contains(report.Edges, e => e.From == "Reporting" && e.To == "Camp" && e.Count > 0);
        Assert.DoesNotContain(report.Edges, e => string.Equals(e.From, e.To, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Json_CarriesSurfaceForSolutionAndGroups_AndScopesWithGroup()
    {
        var report = await ScoreAsync();

        var json = Capture(() => SurfaceScoreCommand.WriteJson(report, null, 5, 5, null));
        using var doc = JsonDocument.Parse(json);
        var surface = doc.RootElement.GetProperty("surface");
        Assert.Equal(report.Surface.ExportedWriteMethods, surface.GetProperty("exported").GetProperty("write").GetInt32());
        Assert.Equal(report.Surface.InternalTypes, surface.GetProperty("internal").GetProperty("types").GetInt32());
        Assert.True(doc.RootElement.GetProperty("edges").GetArrayLength() > 0);
        foreach (var g in doc.RootElement.GetProperty("groups").EnumerateArray())
        {
            var name = g.GetProperty("name").GetString()!;
            Assert.Equal(report.SurfaceBySection[name].ExportedTypes,
                g.GetProperty("surface").GetProperty("exported").GetProperty("types").GetInt32());
        }

        var scoped = Capture(() => SurfaceScoreCommand.WriteJson(report, "Camp", 5, 5, null));
        using var scopedDoc = JsonDocument.Parse(scoped);
        Assert.Equal(report.SurfaceBySection["Camp"].ExportedTypes,
            scopedDoc.RootElement.GetProperty("surface").GetProperty("exported").GetProperty("types").GetInt32());
        Assert.All(scopedDoc.RootElement.GetProperty("edges").EnumerateArray(),
            e => Assert.True(e.GetProperty("from").GetString() == "Camp" || e.GetProperty("to").GetString() == "Camp"));
    }

    [Fact]
    public async Task Compact_PrintsTheCensusLine()
    {
        var report = await ScoreAsync();
        var text = Capture(() => SurfaceScoreCommand.WriteCompact(report, null, 5, 5, null));
        Assert.Contains($"surface: exported: types={report.Surface.ExportedTypes} read={report.Surface.ExportedReadMethods} write={report.Surface.ExportedWriteMethods}", text);
    }

    // ---------------- snapshot --by-assembly ----------------

    [Fact]
    public async Task SectionSnapshot_OneRowPerSectionPlusRollup_MatchingTheReport()
    {
        var report = await ScoreAsync();
        var rows = await SectionSnapshotAnalyzer.AnalyzeAsync(_fixture.Solution, null, CancellationToken.None);

        Assert.Equal(report.MetricsBySection.Count + 1, rows.Count);
        Assert.Equal(SectionSnapshotAnalyzer.SolutionRow, rows[^1].Section);
        var camp = rows.Single(r => r.Section == "Camp");
        Assert.Equal(report.MetricsBySection["Camp"].LocProd, camp.LocProd);
        Assert.Equal(report.SurfaceBySection["Camp"].ExportedWriteMethods, camp.ExportedWriteMethods);
        Assert.Equal(report.TestsBySection["Camp"].Loc, camp.TestLoc);
        Assert.Equal(report.Surface.ExportedTypes, rows[^1].ExportedTypes);
    }

    [Fact]
    public async Task SectionSnapshot_CsvHasOneCellPerHeader_AndAppendWritesHeaderOnce()
    {
        var rows = await SectionSnapshotAnalyzer.AnalyzeAsync(_fixture.Solution, null, CancellationToken.None);

        var csv = SectionSnapshotAnalyzer.ToCsv(rows, header: true).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(rows.Count + 1, csv.Length);
        Assert.Equal(string.Join(",", SectionSnapshotAnalyzer.CsvHeader), csv[0].TrimEnd('\r'));
        Assert.All(csv, line => Assert.Equal(SectionSnapshotAnalyzer.CsvHeader.Length, line.Split(',').Length));

        var path = Path.GetTempFileName();
        try
        {
            SectionSnapshotAnalyzer.AppendCsv(rows, path);
            SectionSnapshotAnalyzer.AppendCsv(rows, path);
            var lines = File.ReadAllLines(path);
            Assert.Equal(1 + rows.Count * 2, lines.Length);
            Assert.Equal(1, lines.Count(l => l.StartsWith("commit_date,")));
        }
        finally { File.Delete(path); }
    }
}
