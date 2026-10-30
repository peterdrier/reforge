namespace Reforge.Tests;

/// <summary>
/// Reused code under the contents census. A declaration with two or more callers skips the flat
/// charge, since merging it away means a copy per caller; and with the sharing factor on, neither a
/// copy per caller nor a forwarding wrapper in front of the shared method lowers the score.
/// </summary>
public class ContentsReuseTests
{
    private const string GateAssembly = "SampleSolution.Gate";

    private const string Ladder = """
        {
                if (v % 2 == 0)
                {
                    for (int i = 0; i < v; i++)
                    {
                        if (i % 3 == 0 && i > 4) v += 1;
                        else if (i % 5 == 0) v -= 1;
                    }
                }
                return v > 100 ? 100 : v;
            }
        """;

    private static string Shared(string extra = "", string callA = "Rate(v)", string callB = "Rate(v)") => $$"""
        namespace SampleSolution.Gate.Rules;

        public class GateReuseService
        {
            public int First(IReadOnlyList<int> values) => values.Sum(v => {{callA}});

            public int Second(IReadOnlyList<int> values) => values.Max(v => {{callB}});

            private static int Rate(int v)
        {{Ladder}}
        {{extra}}
        }
        """;

    [Fact]
    public async Task ReusedDeclaration_SkipsTheFlatCharge()
    {
        var (shared, single) = await ScoreBothAsync(Shared(), Shared(callB: "v"), SurfaceScoreConfig.Default());
        int m = SurfaceScoreConfig.Default().Weight("methodCount");
        Assert.Equal(single.ByRule.GetValueOrDefault("methodCount") - m, shared.ByRule.GetValueOrDefault("methodCount"));
    }

    [Fact]
    public async Task WithSharingOn_CopyingOrWrappingSharedCode_NeverLowersTheScore()
    {
        var config = SurfaceScoreConfig.Default();
        config.ComplexitySharingPercent = 20;

        var baseline = await ScoreAsync(Shared(), config);
        var copied = await ScoreAsync(Shared($"\n    private static int RateAgain(int v)\n{Ladder}", callB: "RateAgain(v)"), config);
        var wrapped = await ScoreAsync(Shared("\n    private static int Forward(int v) => Rate(v);",
            callA: "Forward(v)", callB: "Forward(v)"), config);

        Assert.True(copied.Total >= baseline.Total, $"a copy per caller scores {copied.Total}, below {baseline.Total}");
        Assert.True(wrapped.Total >= baseline.Total, $"a forwarding wrapper scores {wrapped.Total}, below {baseline.Total}");
    }

    private static async Task<(ScoreReport, ScoreReport)> ScoreBothAsync(string a, string b, SurfaceScoreConfig config)
        => (await ScoreAsync(a, config), await ScoreAsync(b, config));

    private static async Task<ScoreReport> ScoreAsync(string source, SurfaceScoreConfig config)
    {
        var root = Path.Combine(Path.GetTempPath(), "reforge-reuse-" + Guid.NewGuid().ToString("N"));
        var rules = Path.Combine(root, GateAssembly, "Rules");
        Directory.CreateDirectory(rules);
        try
        {
            var file = Path.Combine(rules, "reuse.cs");
            await File.WriteAllTextAsync(file, source);
            var report = await IsolatedVariantScorer.ScoreAsync(file, GateAssembly, root, config);
            Assert.False(report.BuildHealth.Degraded,
                "variant does not compile: " + string.Join("; ", report.BuildHealth.Diagnostics.Take(3).Select(d => d.Message)));
            return report;
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
