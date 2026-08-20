using System.Text.Json;
using Reforge.Commands;

namespace Reforge.Tests;

/// <summary>
/// Issue #9: a --baseline comparison must not silently mix build states. An unbuilt/degraded
/// workspace under-resolves cross-section/DI/entity-return rules, so comparing a baseline
/// captured on one build state against a current run on another produces a physically
/// impossible-looking "regression" that is actually a measurement artifact. These tests cover
/// SurfaceScoreBaseline's build-state guard plus its surfacing across all three output formats.
/// </summary>
public class SurfaceScoreBaselineBuildStateTests
{
    private static string WriteBaseline(string buildBlock)
    {
        var json = $$"""
            { "total": 0, "surfaceTotal": 0, "implementationShapeTotal": 0, "byRule": {}, "groups": []{{buildBlock}} }
            """;
        var path = Path.GetTempFileName();
        File.WriteAllText(path, json);
        return path;
    }

    // ---------------- SurfaceScoreBaseline.Compare: guard logic ----------------

    [Fact]
    public void Compare_UnbuiltBaselineVsCleanCurrent_FlagsMismatch()
    {
        var path = WriteBaseline(""", "build": { "degraded": false, "appearsUnbuilt": true } """);
        try
        {
            var now = new ScoreReport(); // default BuildHealth: not degraded, not unbuilt
            var cmp = SurfaceScoreBaseline.Compare(now, path);

            Assert.True(cmp.BuildStateMismatch);
            Assert.Contains("appearsUnbuilt=true", cmp.BuildStateMismatchMessage);
            Assert.Contains("compiled cleanly", cmp.BuildStateMismatchMessage);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Compare_MatchingCleanStates_NoMismatch()
    {
        var path = WriteBaseline(""", "build": { "degraded": false, "appearsUnbuilt": false } """);
        try
        {
            var now = new ScoreReport();
            var cmp = SurfaceScoreBaseline.Compare(now, path);

            Assert.False(cmp.BuildStateMismatch);
            Assert.Null(cmp.BuildStateMismatchMessage);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Compare_MatchingDegradedStates_NoMismatch()
    {
        var path = WriteBaseline(""", "build": { "degraded": true, "appearsUnbuilt": true } """);
        try
        {
            var now = new ScoreReport { BuildHealth = new BuildHealth(true, 3, 2, true) };
            var cmp = SurfaceScoreBaseline.Compare(now, path);

            Assert.False(cmp.BuildStateMismatch);
            Assert.Null(cmp.BuildStateMismatchMessage);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Compare_BaselineMissingBuildBlock_TreatedAsUnknown_FlagsMismatch()
    {
        // No "build" key at all -- a pre-v0.20 baseline. Must be treated as unknown, not as clean,
        // so it always flags a mismatch even against a clean current run.
        var path = WriteBaseline("");
        try
        {
            var now = new ScoreReport();
            var cmp = SurfaceScoreBaseline.Compare(now, path);

            Assert.True(cmp.BuildStateMismatch);
            Assert.Contains("unknown build state", cmp.BuildStateMismatchMessage);
        }
        finally { File.Delete(path); }
    }

    // ---------------- Command output: surfaces across all three formats ----------------

    private static string Capture(Action action)
    {
        var original = Console.Out;
        var sw = new StringWriter();
        Console.SetOut(sw);
        try { action(); }
        finally { Console.SetOut(original); }
        return sw.ToString();
    }

    /// <summary>Mimics the wiring in SurfaceScoreCommand.Create: run Compare, then add the
    /// mismatch diagnostic to report.Diagnostics exactly as the real command does.</summary>
    private static (ScoreReport report, BaselineComparison baseline) MismatchedComparison()
    {
        var path = WriteBaseline(""", "build": { "degraded": false, "appearsUnbuilt": true } """);
        try
        {
            var report = new ScoreReport();
            var baseline = SurfaceScoreBaseline.Compare(report, path);
            if (baseline.BuildStateMismatch)
                report.Diagnostics.Add(new ScoreDiagnostic("warning", "baseline-build-state-mismatch",
                    baseline.BuildStateMismatchMessage!));
            return (report, baseline);
        }
        finally { File.Delete(path); }
    }

    private static (ScoreReport report, BaselineComparison baseline) MatchedComparison()
    {
        var path = WriteBaseline(""", "build": { "degraded": false, "appearsUnbuilt": false } """);
        try
        {
            var report = new ScoreReport();
            var baseline = SurfaceScoreBaseline.Compare(report, path);
            if (baseline.BuildStateMismatch)
                report.Diagnostics.Add(new ScoreDiagnostic("warning", "baseline-build-state-mismatch",
                    baseline.BuildStateMismatchMessage!));
            return (report, baseline);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Compact_Mismatch_MarksLowConfidenceAndShowsWarning()
    {
        var (report, baseline) = MismatchedComparison();
        var output = Capture(() => SurfaceScoreCommand.WriteCompact(report, null, 10, 25, baseline));

        Assert.Contains("lowConfidence=true", output);
        Assert.Contains("appearsUnbuilt=true", output);
    }

    [Fact]
    public void Markdown_Mismatch_MarksLowConfidenceAndShowsDiagnostic()
    {
        var (report, baseline) = MismatchedComparison();
        var output = Capture(() => SurfaceScoreCommand.WriteMarkdown(report, null, 10, 25, baseline));

        Assert.Contains("LOW CONFIDENCE", output);
        Assert.Contains("baseline-build-state-mismatch", output);
    }

    [Fact]
    public void Json_Mismatch_MarksLowConfidenceAndListsDiagnostic()
    {
        var (report, baseline) = MismatchedComparison();
        var output = Capture(() => SurfaceScoreCommand.WriteJson(report, null, 10, 25, baseline));

        using var doc = JsonDocument.Parse(output);
        var baselineEl = doc.RootElement.GetProperty("baseline");
        Assert.True(baselineEl.GetProperty("lowConfidence").GetBoolean());

        var diagnostics = doc.RootElement.GetProperty("diagnostics");
        Assert.Contains(diagnostics.EnumerateArray(), d => d.GetProperty("code").GetString() == "baseline-build-state-mismatch");
    }

    [Fact]
    public void MatchingStates_NoDiagnosticAndOutputUnchanged()
    {
        var (report, baseline) = MatchedComparison();

        Assert.DoesNotContain(report.Diagnostics, d => d.Code == "baseline-build-state-mismatch");

        var compact = Capture(() => SurfaceScoreCommand.WriteCompact(report, null, 10, 25, baseline));
        Assert.DoesNotContain("lowConfidence", compact);

        var markdown = Capture(() => SurfaceScoreCommand.WriteMarkdown(report, null, 10, 25, baseline));
        Assert.DoesNotContain("LOW CONFIDENCE", markdown);

        var json = Capture(() => SurfaceScoreCommand.WriteJson(report, null, 10, 25, baseline));
        using var doc = JsonDocument.Parse(json);
        var baselineEl = doc.RootElement.GetProperty("baseline");
        Assert.False(baselineEl.TryGetProperty("lowConfidence", out _));
    }
}
