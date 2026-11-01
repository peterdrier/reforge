using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Reforge.Commands;

namespace Reforge.Tests;

/// <summary>What the compact report leaves out because it would only repeat itself.</summary>
public class SurfaceScoreCompactOutputTests
{
    private static ScoreReport OneGroup()
    {
        var census = new SurfaceCensus(4, 2, 9, 3, 5, 1, 4, 12, 3);
        var metrics = new SectionMetrics(1000, 10, 8, 2, 40, MetricDistribution.Empty, MetricDistribution.Empty, 300, "CampService");
        var report = new ScoreReport { Total = 42, ImplementationShapeTotal = 42, Surface = census, Metrics = metrics };
        report.ByRule["methodComplexity"] = 42;
        var camp = new GroupScore { Name = "Camp", Total = 42, ImplementationShapeTotal = 42, Surface = census, Metrics = metrics };
        camp.ByRule["methodComplexity"] = 42;
        camp.Entries.Add(new ScoreEntry("methodComplexity", 42, "Run", "Camp", "Camp.cs", 3, "CampService.Run (CC 20)"));
        report.Groups["Camp"] = camp;
        report.ConfiguredSections.Add("Camp");
        report.MetricsBySection["Camp"] = metrics;
        report.SurfaceBySection["Camp"] = census;
        return report;
    }

    [Fact]
    public void OneGroup_DoesNotRepeatTheHeaderLines()
    {
        var text = Capture(() => SurfaceScoreCommand.WriteCompact(OneGroup(), null, 10, 0, null));
        Assert.Single(text.Split('\n'), l => l.Contains("loc=1000"));
        Assert.Single(text.Split('\n'), l => l.Contains("exported: types=4"));
        Assert.Contains("total=42", text);
        Assert.DoesNotContain("combined", text);
        Assert.DoesNotContain("informational", text);
    }

    [Fact]
    public void Glossary_OnlyWithExplain()
    {
        var plain = Capture(() => SurfaceScoreCommand.WriteCompact(OneGroup(), null, 10, 0, null));
        var explained = Capture(() => SurfaceScoreCommand.WriteCompact(OneGroup(), null, 10, 0, null, explain: true));
        Assert.DoesNotContain("Rules:", plain);
        Assert.Contains("Rules:", explained);
    }

    [Fact]
    public void UnusedExternally_CollapsesWhenItRepeatsTheExportedCounts()
    {
        var report = OneGroup();
        Assert.Contains("unused-externally=all", Capture(() => SurfaceScoreCommand.WriteCompact(report, null, 10, 0, null)));

        report.Surface = new SurfaceCensus(4, 2, 9, 3, 5, 1, 2, 6, 1);
        Assert.Contains("unused-externally types=2 methods=6 write=1", Capture(() => SurfaceScoreCommand.WriteCompact(report, null, 10, 0, null)));
    }

    [Theory]
    [InlineData("Run", "Run (CC 20)", "C.Run (CC 20)")]
    [InlineData("Run", null, "C.Run")]
    [InlineData("Run", "RunAll", "RunAll")]
    [InlineData("Run", "Run(flag)", "C.Run(flag)")]
    public void MethodDetails_LeadWithTheContainingType(string method, string? detail, string expected)
    {
        var tree = CSharpSyntaxTree.ParseText($"class C {{ void {method}() {{ }} }}");
        var symbol = CSharpCompilation.Create("t", [tree]).GetTypeByMetadataName("C")!.GetMembers(method).Single();
        Assert.Equal(expected, SurfaceScoreEngine.QualifyMethod(symbol, detail));
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
