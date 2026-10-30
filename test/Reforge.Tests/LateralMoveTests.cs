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
        ("event initializer", Service($"private static event Func<int, int> Classify = id =>\n{Body};")),
        ("private nested type", Service($"private static class Rules\n{{\n    internal static int Classify(int id)\n{Body}\n}}", "Rules.Classify(id)")),
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

    // Reconcile (CC 3) calls Classify (CC 15), which carries its caller's log, so the helper needs
    // state from its caller wherever it moves.
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

    private static string CrossTypeService(string call, string types = "", string field = "", string helper = "") => $$"""
        namespace SampleSolution.Gate.Rules;

        public class GateCrossTypeService
        {
            private readonly List<string> _log = new();
            {{field}}
        {{Entry.Replace("CALL(id)", call)}}
            {{helper}}
        }

        {{types}}
        """;

    /// <summary>The helper behind a one-member interface, called through a field of <paramref name="fieldType"/>.</summary>
    private static string BehindInterface(string fieldType, string implementations) => CrossTypeService(
        "_classifier.Classify(_log, id)",
        field: $"private readonly {fieldType} _classifier = new GateClassifier();",
        types: $$"""
            internal interface IGateClassifier
            {
                int Classify(List<string> log, int id);
            }

            {{implementations}}
            """);

    private static readonly string ImplicitClassifier = $$"""
        internal sealed class GateClassifier : IGateClassifier
        {
            public int Classify(List<string> log, int id)
        {{ClassifyBody}}
        }
        """;

    /// <summary>The even branch of <see cref="ClassifyBody"/>, lifted into a helper of its own.</summary>
    private const string EvenHelper = """
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
        """;

    private static string OddHelper(string evenCall) => $$"""
        {
                if (id % 2 == 0)
                {
                    return {{evenCall}}(log, id);
                }
                if (id > 50 && id % 4 == 0) log.Add("big");
                return id % 7 == 0 ? 7 : 1;
            }
        """;

    /// <summary>
    /// Moving a single-caller helper into another type (#76): widened and moved out, hidden behind
    /// a one-member interface it is the only implementation of, implemented explicitly, or sharing
    /// the interface with an explicit implementation beside it. Then a chain of two helpers moved one
    /// type per hop, against the same chain in one type.
    /// </summary>
    [Fact]
    public async Task MovingTheHelperToAnotherType_NeverLowersTheScore()
    {
        var failures = new List<string>();
        var before = CrossTypeService("Classify(_log, id)",
            helper: $"private static int Classify(List<string> log, int id)\n{ClassifyBody}");
        string Moved(string access) => CrossTypeService("GateClassifier.Classify(_log, id)",
            types: $"{access} static class GateClassifier\n{{\n    {access} static int Classify(List<string> log, int id)\n{ClassifyBody}\n}}");
        await AssertNoneLowerAsync(("private helper", before), new[]
        {
            ("internal type", Moved("internal")),
            ("public type", Moved("public")),
            ("through the interface", BehindInterface("IGateClassifier", ImplicitClassifier)),
            ("through the implementation", BehindInterface("GateClassifier", ImplicitClassifier)),
            ("explicit implementation", BehindInterface("IGateClassifier", $$"""
                internal sealed class GateClassifier : IGateClassifier
                {
                    int IGateClassifier.Classify(List<string> log, int id)
                {{ClassifyBody}}
                }
                """)),
            ("beside an explicit implementation", BehindInterface("IGateClassifier", ImplicitClassifier + """

                internal sealed class GateNullClassifier : IGateClassifier
                {
                    int IGateClassifier.Classify(List<string> log, int id) => -id;
                }
                """)),
        }, failures);

        var split = CrossTypeService("Classify(_log, id)", helper:
            $"private static int Classify(List<string> log, int id)\n{OddHelper("ClassifyEven")}\n"
            + $"    private static int ClassifyEven(List<string> log, int id)\n{EvenHelper}");
        var movedTwice = CrossTypeService("GateClassifier.Classify(_log, id)", types:
            $"internal static class GateClassifier\n{{\n    internal static int Classify(List<string> log, int id)\n{OddHelper("GateEvenClassifier.ClassifyEven")}\n}}\n\n"
            + $"internal static class GateEvenClassifier\n{{\n    internal static int ClassifyEven(List<string> log, int id)\n{EvenHelper}\n}}");
        await AssertNoneLowerAsync(("chain in one type", split), new[] { ("moved twice", movedTwice) }, failures);

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    /// <summary>
    /// A dispatcher calling one method in each of six types is priced as itself, as when the six are
    /// private helpers beside it: each target is charged its own complexity wherever it sits, and
    /// nothing is rolled up into the dispatcher either way.
    /// </summary>
    [Fact]
    public async Task ACompositionRoot_IsPricedAsItselfWhereverItsTargetsSit()
    {
        const int targets = 6;
        var body = ClassifyBody.Replace("return 6;", "if (id > 1000) { if (id % 13 == 0) { log.Add(\"x\"); } } return 6;");
        string Root(Func<int, string> call, string members, string types) => $$"""
            namespace SampleSolution.Gate.Rules;

            public static class GateDispatcher
            {
                public static int Dispatch(int command, List<string> log, int n) => command switch
                {
            {{string.Join(Environment.NewLine, Enumerable.Range(0, targets).Select(i => $"            {i} => {call(i)}(log, n),"))}}
                    _ => 0,
                };

                {{members}}
            }

            {{types}}
            """;
        var inType = Root(i => $"Run{i}", string.Join(Environment.NewLine,
            Enumerable.Range(0, targets).Select(i => $"private static int Run{i}(List<string> log, int id)\n{body}")), "");
        var spread = Root(i => $"GateTarget{i}.Run", "", string.Join(Environment.NewLine,
            Enumerable.Range(0, targets).Select(i => $"internal static class GateTarget{i}\n{{\n    internal static int Run(List<string> log, int id)\n{body}\n}}")));

        var failures = new List<string>();
        var scores = await AssertNoneLowerAsync(("targets in its type", inType),
            new[] { ("targets in their own types", spread) }, failures);
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));

        foreach (var (name, report) in scores)
            Assert.True(Charged(report, "Run").Count() == targets, $"'{name}': every target must be charged on its own.");
        Assert.Equal(Charged(scores[0].Report, "Dispatch").Sum(e => e.Points),
            Charged(scores[1].Report, "Dispatch").Sum(e => e.Points));

        static IEnumerable<ScoreEntry> Charged(ScoreReport r, string name) => r.Groups.Values.SelectMany(g => g.Entries)
            .Where(e => e.Rule == "methodComplexity" && e.Symbol.Contains(name, StringComparison.Ordinal));
    }

    /// <summary>
    /// Two methods that call each other cost the same split across two classes as in one, on either
    /// side of the threshold: a cycle is two declarations, each charged once from its own root.
    /// </summary>
    [Fact]
    public async Task SplittingMutualRecursionAcrossTypes_NeverLowersTheScore()
    {
        static string Method(string name, string other, string extra, int mod) => $$"""
                public static int {{name}}(List<string> log, int n)
                {
                    {{extra}}
                    if (n <= 0) return 0;
                    if (n % {{mod}} == 0)
                    {
                        for (int i = 0; i < n; i++)
                            if (i % 3 == 0) log.Add("{{name}}");
                    }
                    return {{other}}(log, n - 1);
                }
            """;
        static string OneType(string extra) => "namespace SampleSolution.Gate.Rules;\n\npublic static class GatePing\n{\n"
            + Method("Ping", "Pong", extra, 2) + "\n" + Method("Pong", "Ping", extra, 3) + "\n}\n";
        static string TwoTypes(string extra) => "namespace SampleSolution.Gate.Rules;\n\npublic static class GatePing\n{\n"
            + Method("Ping", "GatePong.Pong", extra, 2) + "\n}\n\npublic static class GatePong\n{\n"
            + Method("Pong", "GatePing.Ping", extra, 3) + "\n}\n";

        var failures = new List<string>();
        foreach (var (label, extra) in new[] { ("small", ""), ("large", "if (n > 100) n = 100; if (log.Count > 50) log.Clear();") })
            await AssertNoneLowerAsync(($"{label} in one type", OneType(extra)),
                new[] { ($"{label} in two types", TwoTypes(extra)) }, failures);
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    /// <summary>
    /// Scores <paramref name="baseline"/> and each variant, and records any variant whose contents or
    /// total falls below the baseline's. The baseline must be charged on the contents axis.
    /// </summary>
    private static async Task<List<(string Name, ScoreReport Report)>> AssertNoneLowerAsync(
        (string Name, string Source) baseline, IEnumerable<(string Name, string Source)> variants, List<string> failures)
    {
        var scores = await ScoreAllAsync(new[] { baseline }.Concat(variants));
        var (baseName, baseReport) = scores[0];
        Assert.True(Contents(baseReport) > 0, $"'{baseName}' must be charged on the contents axis.");
        foreach (var (name, report) in scores.Skip(1))
        {
            if (Contents(report) < Contents(baseReport))
                failures.Add($"'{name}': contents {Contents(report)}, below '{baseName}' at {Contents(baseReport)}");
            if (report.Total < baseReport.Total)
                failures.Add($"'{name}': total {report.Total}, below '{baseName}' at {baseReport.Total}");
        }
        return scores;
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
