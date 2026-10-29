using System.Text.Json;
using Reforge.Commands;

namespace Reforge.Tests;

/// <summary>
/// Coverage reports read from a test run and attributed to sections by assembly. Reforge never
/// collects coverage itself; it joins what Coverlet or dotCover wrote onto the section map.
/// </summary>
[Collection("SampleSolution")]
public class CoverageReportTests
{
    private readonly SampleSolutionFixture _fixture;
    public CoverageReportTests(SampleSolutionFixture fixture) => _fixture = fixture;

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

    private static string WriteTemp(string xml)
    {
        var path = Path.Combine(Path.GetTempPath(), $"reforge-cov-{Guid.NewGuid():N}.xml");
        File.WriteAllText(path, xml);
        return path;
    }

    // Coverlet layout: package per assembly, lines under classes, hits per line. Camp and
    // Camp.Contracts are two packages that must fold into one section; Camp.Tests is a package
    // the solution has no section for.
    private const string Cobertura = """
        <?xml version="1.0" encoding="utf-8"?>
        <coverage line-rate="0.5" branch-rate="0" version="1.9" timestamp="1" lines-covered="5" lines-valid="10">
          <packages>
            <package name="SampleSolution.Camp" line-rate="0.5">
              <classes>
                <class name="SampleSolution.Camp.CampService" filename="CampService.cs" line-rate="0.5">
                  <methods>
                    <method name="GetAsync" signature="()"><lines><line number="10" hits="3"/><line number="11" hits="3"/></lines></method>
                    <method name="SaveAsync" signature="()"><lines><line number="20" hits="0"/><line number="21" hits="0"/></lines></method>
                    <method name="Lambda" signature="()"><lines><line number="11" hits="1"/></lines></method>
                  </methods>
                  <lines>
                    <line number="10" hits="3"/><line number="11" hits="3"/><line number="20" hits="0"/><line number="21" hits="0"/>
                  </lines>
                </class>
              </classes>
            </package>
            <package name="SampleSolution.Camp.Contracts" line-rate="1">
              <classes>
                <class name="SampleSolution.Camp.Contracts.CampInfo" filename="CampInfo.cs" line-rate="1">
                  <lines><line number="5" hits="1"/><line number="6" hits="1"/></lines>
                </class>
              </classes>
            </package>
            <package name="SampleSolution.Services" line-rate="0">
              <classes>
                <class name="SampleSolution.Services.UserService" filename="UserService.cs" line-rate="0">
                  <lines><line number="1" hits="0"/><line number="2" hits="0"/><line number="3" hits="0"/><line number="4" hits="0"/></lines>
                </class>
              </classes>
            </package>
            <package name="SampleSolution.Camp.Tests" line-rate="1">
              <classes>
                <class name="SampleSolution.Camp.Tests.CampTests" filename="CampTests.cs" line-rate="1">
                  <lines><line number="1" hits="1"/></lines>
                </class>
              </classes>
            </package>
          </packages>
        </coverage>
        """;

    private const string DotCover = """
        <?xml version="1.0" encoding="utf-8"?>
        <Root CoveredStatements="30" TotalStatements="100" CoveragePercent="30" ReportType="DetailedXml" DotCoverVersion="2025.2">
          <FileIndices><File Index="1" Name="C:\src\CampService.cs" /></FileIndices>
          <Assembly Name="SampleSolution.Camp" CoveredStatements="20" TotalStatements="40" CoveragePercent="50">
            <Namespace Name="SampleSolution.Camp" CoveredStatements="20" TotalStatements="40" CoveragePercent="50">
              <Type Name="CampService" CoveredStatements="20" TotalStatements="40" CoveragePercent="50">
                <Method Name="GetAsync():Task" CoveredStatements="20" TotalStatements="40" CoveragePercent="50" />
              </Type>
            </Namespace>
          </Assembly>
          <Assembly Name="SampleSolution.Camp.Contracts" CoveredStatements="10" TotalStatements="10" CoveragePercent="100" />
          <Assembly Name="SampleSolution.Lodge" CoveredStatements="0" TotalStatements="50" CoveragePercent="0" />
          <Assembly Name="SampleSolution.Lodge.IntegrationTests" CoveredStatements="9" TotalStatements="9" CoveragePercent="100" />
        </Root>
        """;

    private static readonly Dictionary<string, string> SectionMap = new(StringComparer.Ordinal)
    {
        ["SampleSolution.Camp"] = "Camp",
        ["SampleSolution.Camp.Contracts"] = "Camp",
        ["SampleSolution.Services"] = "Services",
        ["SampleSolution.Lodge"] = "Lodge",
    };

    [Fact]
    public void Cobertura_CountsLinesPerClassOnce_FoldsContracts_AndLeavesUnknownAssembliesOut()
    {
        var path = WriteTemp(Cobertura);
        try
        {
            var ingest = CoverageReportReader.Read(path, SectionMap);

            Assert.Equal("cobertura", ingest.Format);
            // Camp: 2 of 4 lines (line 11 listed twice counts once) + Contracts 2 of 2.
            Assert.Equal(new SectionCoverage(4, 6), ingest.BySection["Camp"]);
            Assert.Equal(67, ingest.BySection["Camp"].Percent);
            Assert.Equal(new SectionCoverage(0, 4), ingest.BySection["Services"]);
            Assert.Equal(new SectionCoverage(4, 10), ingest.Solution);
            Assert.Equal(["SampleSolution.Camp.Tests"], ingest.UnmatchedAssemblies);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void DotCover_ReadsAssemblyStatementCounts()
    {
        var path = WriteTemp(DotCover);
        try
        {
            var ingest = CoverageReportReader.Read(path, SectionMap);

            Assert.Equal("dotcover", ingest.Format);
            Assert.Equal(new SectionCoverage(30, 50), ingest.BySection["Camp"]);
            Assert.Equal(new SectionCoverage(0, 50), ingest.BySection["Lodge"]);
            Assert.True(ingest.BySection["Lodge"].HasData);
            Assert.Equal(0, ingest.BySection["Lodge"].Percent);
            Assert.Equal(new SectionCoverage(30, 100), ingest.Solution);
            Assert.Equal(["SampleSolution.Lodge.IntegrationTests"], ingest.UnmatchedAssemblies);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void UnknownFormat_IsRefusedWithTheRootElementNamed()
    {
        var path = WriteTemp("<report><thing/></report>");
        try
        {
            var ex = Assert.Throws<InvalidDataException>(() => CoverageReportReader.Read(path, SectionMap));
            Assert.Contains("<report>", ex.Message);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Report_AttachesCoverageToGroups_AndNamesWhatIsMissing()
    {
        var report = await ScoreAsync();
        var path = WriteTemp(Cobertura);
        try
        {
            // The engine's own assembly map is the join key the command uses.
            Assert.Equal("Camp", report.SectionByAssembly["SampleSolution.Camp.Contracts"]);
            report.AttachCoverage(CoverageReportReader.Read(path, report.SectionByAssembly));

            Assert.Equal("cobertura", report.CoverageFormat);
            Assert.Equal(67, report.Groups["Camp"].Coverage.Percent);
            Assert.Same(report.CoverageBySection["Camp"], report.Groups["Camp"].Coverage);
            Assert.False(report.Groups["Web"].Coverage.HasData);
            Assert.Contains(report.Diagnostics, d => d.Code == "coverage-unmatched-assembly" && d.Message.Contains("SampleSolution.Camp.Tests"));
            Assert.Contains(report.Diagnostics, d => d.Code == "coverage-missing-section" && d.Message.Contains("Web") && !d.Message.Contains("Camp"));
            // Informational: attaching coverage moves no score.
            var before = await ScoreAsync();
            Assert.Equal(before.Total, report.Total);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Json_DistinguishesUnmeasuredFromZero()
    {
        var report = await ScoreAsync();
        var path = WriteTemp(Cobertura);
        try
        {
            report.AttachCoverage(CoverageReportReader.Read(path, report.SectionByAssembly));
            var json = Capture(() => SurfaceScoreCommand.WriteJson(report, null, 5, 5, null));
            using var doc = JsonDocument.Parse(json);

            Assert.Equal("cobertura", doc.RootElement.GetProperty("coverageFormat").GetString());
            Assert.Equal(40, doc.RootElement.GetProperty("coverage").GetProperty("percent").GetInt32());
            var groups = doc.RootElement.GetProperty("groups").EnumerateArray().ToDictionary(g => g.GetProperty("name").GetString()!);
            Assert.Equal(0, groups["Services"].GetProperty("coverage").GetProperty("percent").GetInt32());
            // Null keys are dropped from the JSON, so unmeasured is an absent key, never a 0.
            Assert.False(groups["Web"].TryGetProperty("coverage", out _));

            var text = Capture(() => SurfaceScoreCommand.WriteCompact(report, null, 5, 5, null));
            Assert.Contains("coverage: 40% (4/10 statements) (cobertura)", text);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task WithoutAReport_NothingMentionsCoverage()
    {
        var report = await ScoreAsync();
        var json = Capture(() => SurfaceScoreCommand.WriteJson(report, null, 5, 5, null));
        using var doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.TryGetProperty("coverage", out _));
        Assert.False(doc.RootElement.TryGetProperty("coverageFormat", out _));
        Assert.DoesNotContain("coverage:", Capture(() => SurfaceScoreCommand.WriteCompact(report, null, 5, 5, null)));
    }

    [Fact]
    public async Task SectionSnapshot_CarriesCoverageColumns_BlankWhereUnmeasured()
    {
        var path = WriteTemp(DotCover);
        try
        {
            var rows = await SectionSnapshotAnalyzer.AnalyzeAsync(_fixture.Solution, path, CancellationToken.None);
            var camp = rows.Single(r => r.Section == "Camp");
            Assert.Equal((30, 50, 60), (camp.CoverageCovered, camp.CoverageTotal, camp.CoveragePercent));
            var web = rows.Single(r => r.Section == "Web");
            Assert.Null(web.CoveragePercent);

            var csv = SectionSnapshotAnalyzer.ToCsv(rows, header: true).Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.All(csv, line => Assert.Equal(SectionSnapshotAnalyzer.CsvHeader.Length, line.Split(',').Length));
            Assert.EndsWith(",30,50,60", csv.Single(l => l.Contains(",Camp,")).TrimEnd('\r'));
            Assert.EndsWith(",,,", csv.Single(l => l.Contains(",Web,")).TrimEnd('\r'));
        }
        finally { File.Delete(path); }
    }
}
