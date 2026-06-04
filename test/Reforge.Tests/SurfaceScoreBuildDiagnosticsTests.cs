using System.Text.Json;
using Reforge.Commands;

namespace Reforge.Tests;

/// <summary>
/// surface-score must surface the ACTUAL workspace compile errors (CS-code + file:line +
/// message + project) in every format when the build is degraded — not just a count.
/// Clean builds must add no new output.
/// </summary>
public class SurfaceScoreBuildDiagnosticsTests
{
    private static ScoreReport DegradedReport(int truncated = 3)
    {
        var bh = new BuildHealth(Degraded: true, CompilationErrorCount: 6, UnresolvedReferenceCount: 4, AppearsUnbuilt: false)
        {
            Diagnostics = new[]
            {
                new BuildDiagnostic("CS0246", "Error", "SampleSolution.Services", "src/Foo.cs", 12, 5,
                    "The type or namespace name 'Bar' could not be found"),
                new BuildDiagnostic("CS0103", "Error", "SampleSolution.Web", "src/Web/Home.cs", 30, 9,
                    "The name 'Helper' does not exist in the current context"),
            },
            DiagnosticsTruncated = truncated
        };
        var report = new ScoreReport { BuildHealth = bh };
        report.Diagnostics.Add(new ScoreDiagnostic("warning", "degraded-build", BuildInspector.DescribeDegraded(bh)));
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

    [Fact]
    public void Compact_Degraded_ListsEachErrorWithCodeFileLineMessageProject()
    {
        var report = DegradedReport();
        var output = Capture(() => SurfaceScoreCommand.WriteCompact(report, null, 10, 25, null));

        Assert.Contains("CS0246", output);
        Assert.Contains("src/Foo.cs:12", output);
        Assert.Contains("could not be found", output);
        Assert.Contains("(SampleSolution.Services)", output);
        Assert.Contains("CS0103", output);
        Assert.Contains("src/Web/Home.cs:30", output);
        Assert.Contains("(+3 more)", output);
    }

    [Fact]
    public void Markdown_Degraded_ListsEachErrorWithCodeFileLineMessageProject()
    {
        var report = DegradedReport();
        var output = Capture(() => SurfaceScoreCommand.WriteMarkdown(report, null, 10, 25, null));

        Assert.Contains("CS0246", output);
        Assert.Contains("src/Foo.cs:12", output);
        Assert.Contains("could not be found", output);
        Assert.Contains("SampleSolution.Services", output);
        Assert.Contains("CS0103", output);
        Assert.Contains("+3 more", output);
    }

    [Fact]
    public void Json_Degraded_BuildDiagnosticsAreAdditiveAndComplete()
    {
        var report = DegradedReport();
        var output = Capture(() => SurfaceScoreCommand.WriteJson(report, null, 10, 25, null));

        using var doc = JsonDocument.Parse(output);
        var build = doc.RootElement.GetProperty("build");

        // Existing fields unchanged.
        Assert.True(build.GetProperty("degraded").GetBoolean());
        Assert.Equal(6, build.GetProperty("compilationErrorCount").GetInt32());
        Assert.Equal(4, build.GetProperty("unresolvedReferenceCount").GetInt32());
        Assert.False(build.GetProperty("appearsUnbuilt").GetBoolean());

        // New additive fields.
        Assert.Equal(3, build.GetProperty("diagnosticsTruncated").GetInt32());
        var diags = build.GetProperty("diagnostics");
        Assert.Equal(2, diags.GetArrayLength());

        var first = diags[0];
        Assert.Equal("CS0246", first.GetProperty("id").GetString());
        Assert.Equal("Error", first.GetProperty("severity").GetString());
        Assert.Equal("SampleSolution.Services", first.GetProperty("project").GetString());
        Assert.Equal("src/Foo.cs", first.GetProperty("file").GetString());
        Assert.Equal(12, first.GetProperty("line").GetInt32());
        Assert.Contains("could not be found", first.GetProperty("message").GetString());
    }

    [Fact]
    public void Json_CleanBuild_OmitsBuildDiagnostics()
    {
        var report = new ScoreReport(); // not degraded; empty diagnostics
        var output = Capture(() => SurfaceScoreCommand.WriteJson(report, null, 10, 25, null));

        using var doc = JsonDocument.Parse(output);
        var build = doc.RootElement.GetProperty("build");

        // Existing fields still present and unchanged.
        Assert.False(build.GetProperty("degraded").GetBoolean());
        Assert.Equal(0, build.GetProperty("compilationErrorCount").GetInt32());

        // Additive fields omitted entirely when clean.
        Assert.False(build.TryGetProperty("diagnostics", out _));
        Assert.False(build.TryGetProperty("diagnosticsTruncated", out _));
    }

    [Fact]
    public void Compact_CleanBuild_PrintsNoBuildDiagnosticLines()
    {
        var report = new ScoreReport();
        var output = Capture(() => SurfaceScoreCommand.WriteCompact(report, null, 10, 25, null));

        Assert.DoesNotContain("CS0", output);
        Assert.DoesNotContain("more)", output);
    }
}
