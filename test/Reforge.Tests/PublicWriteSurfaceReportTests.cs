using System.Text.Json;
using Reforge.Commands;

namespace Reforge.Tests;

/// <summary>
/// <c>publicWriteSurface</c> is reported per section and never scored, so it must survive a section
/// that scores nothing. A section whose published interface charges no points — no chargeable
/// methods, or <c>fullServiceInterfaceMethod</c> weighted to zero — has no <see cref="GroupScore"/>
/// at all, because zero-point entries are dropped. Reporting it off the scored groups would hide it
/// in the one case worth seeing most: write capability published for free.
/// </summary>
public class PublicWriteSurfaceReportTests
{
    [Fact]
    public void Json_ReportsASectionThatPublishesWriteSurfaceAndScoresNothing()
    {
        var report = ReportWithUnscoredPublisher();

        using var doc = JsonDocument.Parse(Capture(() => SurfaceScoreCommand.WriteJson(report, null, 10, 25, null)));
        var block = doc.RootElement.GetProperty("publicWriteSurface");

        Assert.Equal(1, block.GetProperty("publishingSections").GetInt32());
        Assert.Equal(1, block.GetProperty("interfaces").GetInt32());
        Assert.Equal(
            "IFreeService",
            block.GetProperty("bySection").GetProperty("Quiet")[0].GetString());
        // The metric's denominator counts sections, not scored groups.
        Assert.Empty(doc.RootElement.GetProperty("groups").EnumerateArray());
        Assert.Equal(1, block.GetProperty("sections").GetInt32());
    }

    [Fact]
    public void Compact_ReportsItBeforeTheNoScoredItemsExit()
    {
        var output = Capture(() => SurfaceScoreCommand.WriteCompact(ReportWithUnscoredPublisher(), null, 10, 25, null));

        Assert.Contains("publicWriteSurface (reported, unscored): 1/1 sections, 1 interfaces", output);
        Assert.Contains("Quiet: IFreeService", output);
        Assert.Contains("(no scored items)", output);
    }

    [Fact]
    public void Markdown_ReportsItBeforeTheNoScoredItemsExit()
    {
        var output = Capture(() => SurfaceScoreCommand.WriteMarkdown(ReportWithUnscoredPublisher(), null, 10, 25, null));

        Assert.Contains("publicWriteSurface", output);
        Assert.Contains("`Quiet`: `IFreeService`", output);
        Assert.Contains("_No scored items found._", output);
    }

    [Fact]
    public void TextFormats_SayZeroRatherThanNothingWhenNoSectionPublishes()
    {
        var report = new ScoreReport();
        report.MetricsBySection["Quiet"] = SectionMetrics.Empty;
        report.ConfiguredSections.Add("Quiet");

        // A measured zero and a report that predates the metric must not look the same.
        Assert.Contains("publicWriteSurface (reported, unscored): 0/1 sections, 0 interfaces",
            Capture(() => SurfaceScoreCommand.WriteCompact(report, null, 10, 25, null)));
        Assert.Contains("0 of 1 sections publish write capability",
            Capture(() => SurfaceScoreCommand.WriteMarkdown(report, null, 10, 25, null)));
    }

    private static ScoreReport ReportWithUnscoredPublisher()
    {
        var report = new ScoreReport();
        // A section with metrics but no group: exactly what the engine produces for a section whose
        // every charge came out at zero points.
        report.MetricsBySection["Quiet"] = SectionMetrics.Empty;
        report.ConfiguredSections.Add("Quiet");
        report.PublicWriteSurface["Quiet"] = new List<string> { "IFreeService" };
        return report;
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
