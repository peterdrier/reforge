namespace Reforge.Tests;

/// <summary>
/// Widening a helper's visibility must never lower a score. The cognitive charge folds a
/// single-caller helper into its caller, so if visibility decided what folds, making the helper
/// internal would split one charged call path into two uncharged methods and pay for an edit that
/// improved nothing (#73).
///
/// <para>Not a Gate 1 pair: a pair's cheapest fix must lower the rule it declares, and the point
/// here is that widening no longer does. The same class is scored once per accessibility of its
/// helper, each variant compiled alone, and the totals must not fall as visibility widens.</para>
/// </summary>
public class HelperVisibilityFoldTests
{
    private const string GateAssembly = "SampleSolution.Gate";

    /// <summary>Narrowest first. Every step widens or holds visibility, so no step may lower the total.</summary>
    private static readonly string[] Ladder =
    {
        "private", "private protected", "protected", "protected virtual",
        "internal", "protected internal", "public",
    };

    // Reconcile (CC 3) and Classify (CC 15) each read at or under the threshold of 15; together
    // they are over it, which is the shape the fold exists to charge and a split used to escape.
    private const string Template = """
        namespace SampleSolution.Gate.Rules;

        public class GateHelperVisibilityService
        {
            private readonly List<string> _log = new();

            public int Reconcile(IReadOnlyList<int> ids)
            {
                int total = 0;
                foreach (var id in ids)
                {
                    if (id < 0) continue;
                    total += Classify(id);
                }
                return total;
            }

            public IReadOnlyList<string> Log() => _log;

            ACCESS int Classify(int id)
            {
                if (id % 2 == 0)
                {
                    if (id % 3 == 0)
                    {
                        for (int i = 0; i < id; i++)
                        {
                            if (i % 5 == 0 && i > 10) _log.Add($"six-{i}");
                            else if (i % 7 == 0) _log.Add($"seven-{i}");
                        }
                        return 6;
                    }
                    while (id > 100) id -= 10;
                    return 2;
                }
                return id % 7 == 0 ? 7 : 1;
            }
        }
        """;

    [Fact]
    public async Task WideningTheHelper_NeverLowersTheScore()
    {
        var root = Path.Combine(Path.GetTempPath(), "reforge-helper-visibility-" + Guid.NewGuid().ToString("N"));
        var rules = Path.Combine(root, GateAssembly, "Rules");
        Directory.CreateDirectory(rules);
        try
        {
            var scores = new List<(string Access, ScoreReport Report)>();
            foreach (var access in Ladder)
            {
                var file = Path.Combine(rules, $"helperVisibility.{access.Replace(' ', '_')}.cs");
                await File.WriteAllTextAsync(file, Template.Replace("ACCESS", access));
                var report = await IsolatedVariantScorer.ScoreAsync(file, GateAssembly, root);
                Assert.False(report.BuildHealth.Degraded, $"'{access}' variant does not compile.");
                scores.Add((access, report));
            }

            int Cog(ScoreReport r) => r.ByRule.GetValueOrDefault("cognitiveComplexity");
            var (_, narrowest) = scores[0];
            Assert.True(Cog(narrowest) > 0, "The private helper must fold into Reconcile and fire the rule.");

            var failures = new List<string>();
            foreach (var (access, report) in scores.Skip(1))
                if (Cog(report) != Cog(narrowest))
                    failures.Add($"'{access}': cognitiveComplexity {Cog(report)}, private {Cog(narrowest)}");
            for (int i = 1; i < scores.Count; i++)
                if (scores[i].Report.Total < scores[i - 1].Report.Total)
                    failures.Add($"'{scores[i].Access}' scores {scores[i].Report.Total}, "
                        + $"below '{scores[i - 1].Access}' at {scores[i - 1].Report.Total}");

            Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
