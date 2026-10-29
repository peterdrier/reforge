namespace Reforge.Tests;

/// <summary>
/// Moving a single-caller helper into another type must never lower a score (#76). The fold
/// only joins a helper to its caller within one type, so a move across types is charged instead:
/// the caller's group pays what the boundary kept under the threshold.
///
/// <para>Not a Gate 1 pair, for the reason <see cref="HelperVisibilityFoldTests"/> gives: a pair's
/// cheapest fix must lower the rule it declares, and the point here is that the move leaves
/// <c>cognitiveComplexity</c> where it was. Each variant is compiled alone. The Before keeps the
/// helper private beside its caller; every other variant widens it and moves it out, so
/// cognitiveComplexity must match the Before exactly and the total must not fall below it.</para>
/// </summary>
public class CrossTypeHelperFoldTests
{
    private const string GateAssembly = "SampleSolution.Gate";
    private const int Threshold = 15;

    // Reconcile (CC 3) and Classify (CC 15) each read at or under the threshold on their own.
    private const string Entry = """
            public int Reconcile(IReadOnlyList<int> ids)
            {
                int total = 0;
                foreach (var id in ids)
                {
                    if (id < 0) continue;
                    total += CALL(id);
                }
                return total;
            }

            public IReadOnlyList<string> Log() => _log;
        """;

    private const string ClassifyBody = """
            {
                if (id % 2 == 0)
                {
                    if (id % 3 == 0)
                    {
                        for (int i = 0; i < id; i++)
                        {
                            if (i % 5 == 0 && i > 10) log.Add($"six-{i}");
                            else if (i % 7 == 0) log.Add($"seven-{i}");
                        }
                        return 6;
                    }
                    while (id > 100) id -= 10;
                    return 2;
                }
                return id % 7 == 0 ? 7 : 1;
            }
        """;

    private static string Before() => $$"""
        namespace SampleSolution.Gate.Rules;

        public class GateCrossTypeService
        {
            private readonly List<string> _log = new();
        {{Entry.Replace("CALL(id)", "Classify(_log, id)")}}
            private static int Classify(List<string> log, int id)
        {{ClassifyBody}}
        }
        """;

    /// <summary>The helper widened to <paramref name="access"/> and moved to a class of its own.</summary>
    private static string Moved(string access) => $$"""
        namespace SampleSolution.Gate.Rules;

        public class GateCrossTypeService
        {
            private readonly List<string> _log = new();
        {{Entry.Replace("CALL(id)", "GateClassifier.Classify(_log, id)")}}
        }

        {{access}} static class GateClassifier
        {
            {{access}} static int Classify(List<string> log, int id)
        {{ClassifyBody}}
        }
        """;

    /// <summary>
    /// The helper moved behind a one-member interface it is the only implementation of, called
    /// through a field of <paramref name="fieldType"/>.
    /// </summary>
    private static string BehindInterface(string fieldType) => $$"""
        namespace SampleSolution.Gate.Rules;

        public class GateCrossTypeService
        {
            private readonly List<string> _log = new();
            private readonly {{fieldType}} _classifier = new GateClassifier();
        {{Entry.Replace("CALL(id)", "_classifier.Classify(_log, id)")}}
        }

        internal interface IGateClassifier
        {
            int Classify(List<string> log, int id);
        }

        internal sealed class GateClassifier : IGateClassifier
        {
            public int Classify(List<string> log, int id)
        {{ClassifyBody}}
        }
        """;

    /// <summary>
    /// The even branch split into a helper of its own, everything still in one type. Lifting the
    /// branch out of its nest is a real reduction, so this is <see cref="MovedTwice"/>'s baseline
    /// rather than <see cref="Before"/>.
    /// </summary>
    private const string Split = """
        namespace SampleSolution.Gate.Rules;

        public class GateCrossTypeService
        {
            private readonly List<string> _log = new();

            public int Reconcile(IReadOnlyList<int> ids)
            {
                int total = 0;
                foreach (var id in ids)
                {
                    if (id < 0) continue;
                    total += Classify(_log, id);
                }
                return total;
            }

            public IReadOnlyList<string> Log() => _log;

            private static int Classify(List<string> log, int id)
            {
                if (id % 2 == 0)
                {
                    return ClassifyEven(log, id);
                }
                if (id > 50 && id % 4 == 0) log.Add("big");
                return id % 7 == 0 ? 7 : 1;
            }

            private static int ClassifyEven(List<string> log, int id)
            {
                if (id % 3 == 0)
                {
                    for (int i = 0; i < id; i++)
                    {
                        if (i % 5 == 0 && i > 10) log.Add($"six-{i}");
                        else if (i % 7 == 0) log.Add($"seven-{i}");
                    }
                    return 6;
                }
                if (id > 1000)
                {
                    if (id % 9 == 0) log.Add("nine");
                }
                while (id > 100) id -= 10;
                return 2;
            }
        }
        """;

    /// <summary>
    /// <see cref="Split"/> with each helper moved into a type of its own: two crossings, one per
    /// hop. The charge has to follow the whole chain, or each hop reads under the threshold alone.
    /// </summary>
    private const string MovedTwice = """
        namespace SampleSolution.Gate.Rules;

        public class GateCrossTypeService
        {
            private readonly List<string> _log = new();

            public int Reconcile(IReadOnlyList<int> ids)
            {
                int total = 0;
                foreach (var id in ids)
                {
                    if (id < 0) continue;
                    total += GateClassifier.Classify(_log, id);
                }
                return total;
            }

            public IReadOnlyList<string> Log() => _log;
        }

        internal static class GateClassifier
        {
            internal static int Classify(List<string> log, int id)
            {
                if (id % 2 == 0)
                {
                    return GateEvenClassifier.ClassifyEven(log, id);
                }
                if (id > 50 && id % 4 == 0) log.Add("big");
                return id % 7 == 0 ? 7 : 1;
            }
        }

        internal static class GateEvenClassifier
        {
            internal static int ClassifyEven(List<string> log, int id)
            {
                if (id % 3 == 0)
                {
                    for (int i = 0; i < id; i++)
                    {
                        if (i % 5 == 0 && i > 10) log.Add($"six-{i}");
                        else if (i % 7 == 0) log.Add($"seven-{i}");
                    }
                    return 6;
                }
                if (id > 1000)
                {
                    if (id % 9 == 0) log.Add("nine");
                }
                while (id > 100) id -= 10;
                return 2;
            }
        }
        """;

    [Fact]
    public async Task MovingTheHelperToAnotherType_NeverLowersTheScore()
    {
        var failures = new List<string>();
        await CompareAsync("one-move", Before(), new()
        {
            ("internal", Moved("internal")),
            ("public", Moved("public")),
            ("interface", BehindInterface("IGateClassifier")),
            ("implementation", BehindInterface("GateClassifier")),
        }, failures);
        await CompareAsync("two-moves", Split, new() { ("movedTwice", MovedTwice) }, failures);
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    private static async Task CompareAsync(string label, string before,
        List<(string Name, string Source)> moved, List<string> failures)
    {
        var scores = await ScoreAllAsync(label, [("before", before), .. moved]);
        var (_, baseline) = scores[0];
        Assert.True(Cog(baseline) > 0, $"{label}: the before must fire cognitiveComplexity.");
        foreach (var (name, report) in scores.Skip(1))
        {
            if (Cog(report) != Cog(baseline))
                failures.Add($"{label} '{name}': cognitiveComplexity {Cog(report)}, before {Cog(baseline)}");
            if (report.Total < baseline.Total)
                failures.Add($"{label} '{name}' scores {report.Total}, below the before at {baseline.Total}");
        }
    }

    /// <summary>
    /// The bound. A dispatcher that calls one method in each of six types, every one of them over
    /// the threshold on its own, hides five thresholds between them. Charging all of them would read
    /// a composition root as the whole program; the dispatcher pays at most one.
    /// </summary>
    [Fact]
    public async Task ACompositionRoot_IsChargedAtMostOneThreshold()
    {
        const int targets = 6;
        var arms = string.Join(Environment.NewLine, Enumerable.Range(0, targets)
            .Select(i => $"            {i} => GateTarget{i}.Run(log, n),"));
        var types = string.Join(Environment.NewLine, Enumerable.Range(0, targets).Select(i => $$"""
            internal static class GateTarget{{i}}
            {
                internal static int Run(List<string> log, int id)
            {{ClassifyBody}}
            }
            """.Replace("return 6;", "if (id > 1000) { if (id % 13 == 0) { log.Add(\"x\"); } } return 6;")));
        var source = $$"""
            namespace SampleSolution.Gate.Rules;

            public static class GateDispatcher
            {
                public static int Dispatch(int command, List<string> log, int n) => command switch
                {
            {{arms}}
                    _ => 0,
                };
            }

            {{types}}
            """;

        var (_, report) = (await ScoreAllAsync("composition-root", new() { ("root", source) }))[0];
        var dispatcher = report.Groups.Values.SelectMany(g => g.Entries)
            .Where(e => e.Rule == "cognitiveComplexity" && e.Symbol.Contains("Dispatch", StringComparison.Ordinal))
            .Sum(e => e.Points);
        var targetsCharged = report.Groups.Values.SelectMany(g => g.Entries)
            .Count(e => e.Rule == "cognitiveComplexity" && e.Symbol.Contains("Run", StringComparison.Ordinal));

        Assert.Equal(targets, targetsCharged);
        Assert.InRange(dispatcher, 1, Threshold);
    }

    private static int Cog(ScoreReport r) => r.ByRule.GetValueOrDefault("cognitiveComplexity");

    private static async Task<List<(string Name, ScoreReport Report)>> ScoreAllAsync(
        string label, List<(string Name, string Source)> variants)
    {
        var root = Path.Combine(Path.GetTempPath(), $"reforge-{label}-" + Guid.NewGuid().ToString("N"));
        var rules = Path.Combine(root, GateAssembly, "Rules");
        Directory.CreateDirectory(rules);
        try
        {
            var scores = new List<(string, ScoreReport)>();
            foreach (var (name, source) in variants)
            {
                var file = Path.Combine(rules, $"{label}.{name}.cs");
                await File.WriteAllTextAsync(file, source);
                var report = await IsolatedVariantScorer.ScoreAsync(file, GateAssembly, root);
                Assert.False(report.BuildHealth.Degraded, $"'{name}' variant does not compile.");
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
