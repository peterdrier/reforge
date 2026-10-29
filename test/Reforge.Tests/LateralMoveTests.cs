namespace Reforge.Tests;

/// <summary>
/// A lateral move — the same code with its helper made wider, moved to another type, or turned
/// into another kind of declaration — must never lower a score. The contents census charges every
/// declaration from its own root whatever its accessibility, so none of these moves the
/// implementation-shape axis down, and widening may only add surface.
///
/// <para>Not Gate 1 pairs: a pair's cheapest fix must lower the rule it declares, and the point here
/// is that these edits lower nothing. Each variant is compiled alone and compared with the helper
/// as a plain private method.</para>
/// </summary>
public class LateralMoveTests
{
    private const string GateAssembly = "SampleSolution.Gate";

    private const string Body = """
        {
                int total = 0;
                if (id % 2 == 0)
                {
                    if (id % 3 == 0)
                    {
                        for (int i = 0; i < id; i++)
                        {
                            if (i % 5 == 0 && i > 10) total += 6;
                            else if (i % 7 == 0) total += 7;
                        }
                        return total;
                    }
                    while (id > 100) id -= 10;
                    return 2;
                }
                return id % 7 == 0 ? 7 : 1;
            }
        """;

    private static string Service(string helperInClass, string call = "Classify(id)", string localHelper = "") => $$"""
        namespace SampleSolution.Gate.Rules;

        public class GateLateralService
        {
            public int Reconcile(IReadOnlyList<int> ids)
            {
                int total = 0;
                foreach (var id in ids)
                {
                    if (id < 0) continue;
                    total += {{call}};
                }
                return total;
                {{localHelper}}
            }

            {{helperInClass}}
        }
        """;

    /// <summary>Narrowest first. Every step widens or holds visibility, so no step may lower the total.</summary>
    private static readonly string[] Ladder =
    {
        "private", "private protected", "protected", "protected virtual",
        "internal", "protected internal", "public",
    };

    private static readonly (string Name, string Source)[] Relocations =
    {
        ("static helper", Service($"private static int Classify(int id)\n{Body}")),
        ("another internal type", Service("", "GateLateralRules.Classify(id)")
            + $"\ninternal static class GateLateralRules\n{{\n    internal static int Classify(int id)\n{Body}\n}}\n"),
        ("another public type", Service("", "GateLateralRules.Classify(id)")
            + $"\npublic static class GateLateralRules\n{{\n    public static int Classify(int id)\n{Body}\n}}\n"),
        ("local function", Service("", localHelper: $"int Classify(int id)\n{Body}")),
        ("lambda in a local", Service("", "classify(id)").Replace("int total = 0;\n        foreach",
            $"int total = 0;\n        Func<int, int> classify = id =>\n{Body};\n        foreach")),
        ("delegate field", Service($"private static readonly Func<int, int> Classify = id =>\n{Body};")),
    };

    [Fact]
    public async Task WideningTheHelper_NeverLowersTheScore()
    {
        var scores = await ScoreAllAsync(Ladder.Select(a => (a, Service($"{a} int Classify(int id)\n{Body}"))));

        var failures = new List<string>();
        var (_, narrowest) = scores[0];
        Assert.True(Contents(narrowest) > 0, "The private helper must be charged on the contents axis.");
        foreach (var (access, report) in scores.Skip(1))
            if (Contents(report) != Contents(narrowest))
                failures.Add($"'{access}': contents {Contents(report)}, private {Contents(narrowest)}");
        for (int i = 1; i < scores.Count; i++)
            if (scores[i].Report.Total < scores[i - 1].Report.Total)
                failures.Add($"'{scores[i].Name}' scores {scores[i].Report.Total}, "
                    + $"below '{scores[i - 1].Name}' at {scores[i - 1].Report.Total}");

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public async Task MovingOrReshapingTheHelper_NeverLowersTheScore()
    {
        var scores = await ScoreAllAsync(
            new[] { ("private method", Service($"private int Classify(int id)\n{Body}")) }.Concat(Relocations));

        var (_, baseline) = scores[0];
        var failures = new List<string>();
        foreach (var (name, report) in scores.Skip(1))
        {
            if (Contents(report) < Contents(baseline))
                failures.Add($"'{name}': contents {Contents(report)}, below the private method's {Contents(baseline)}");
            if (report.Total < baseline.Total)
                failures.Add($"'{name}': total {report.Total}, below the private method's {baseline.Total}");
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    private static int Contents(ScoreReport r)
        => r.ByRule.GetValueOrDefault("methodComplexity") + r.ByRule.GetValueOrDefault("methodCount");

    private static async Task<List<(string Name, ScoreReport Report)>> ScoreAllAsync(IEnumerable<(string Name, string Source)> variants)
    {
        var root = Path.Combine(Path.GetTempPath(), "reforge-lateral-" + Guid.NewGuid().ToString("N"));
        var rules = Path.Combine(root, GateAssembly, "Rules");
        Directory.CreateDirectory(rules);
        try
        {
            var scores = new List<(string, ScoreReport)>();
            foreach (var (name, source) in variants)
            {
                var file = Path.Combine(rules, $"lateral.{new string(name.Where(char.IsLetter).ToArray())}.cs");
                await File.WriteAllTextAsync(file, source);
                var report = await IsolatedVariantScorer.ScoreAsync(file, GateAssembly, root);
                Assert.False(report.BuildHealth.Degraded,
                    $"'{name}' variant does not compile: "
                    + string.Join("; ", report.BuildHealth.Diagnostics.Take(3).Select(d => d.Message)));
                scores.Add((name, report));
            }
            return scores;
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
