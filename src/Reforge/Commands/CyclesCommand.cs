using System.CommandLine;
using System.Diagnostics;
using System.Text.Json;

namespace Reforge.Commands;

public static class CyclesCommand
{
    public static Command Create(Option<string?> solutionOption, Option<OutputFormat> formatOption, Option<int?> limitOption)
    {
        var suggestOption = new Option<bool>("--suggest")
        {
            Description = "Rank top files inside the core SCC by betweenness centrality (highest-leverage fix targets)"
        };
        var topOption = new Option<int>("--top")
        {
            Description = "When --suggest is set, number of fix targets to show (default: 5)",
            DefaultValueFactory = _ => 5
        };

        var command = new Command("cycles", "List circular file dependencies (SCCs) and highest-leverage refactoring targets")
        {
            suggestOption, topOption
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var solutionPath = parseResult.GetValue(solutionOption);
            var format = parseResult.GetValue(formatOption);
            var suggest = parseResult.GetValue(suggestOption);
            var top = parseResult.GetValue(topOption);
            var sw = Stopwatch.StartNew();

            var (solution, handle) = await WorkspaceHelper.OpenSolutionAsync(solutionPath);
            using (handle)
            {
                var graph = await FileDependencyGraph.BuildAsync(solution, cancellationToken);
                var sccs = StructuralAnalysis.FindStronglyConnectedComponents(graph.Adj);

                var nonTrivial = sccs.Where(s => s.Length >= 2)
                    .OrderByDescending(s => s.Length)
                    .ToList();

                if (format == OutputFormat.Json)
                    WriteJson(graph, nonTrivial, suggest, top);
                else
                    WriteCompact(graph, nonTrivial, suggest, top);

                sw.Stop();
                Telemetry.Log("cycles", suggest ? "suggest" : "list", nonTrivial.Count, sw.ElapsedMilliseconds);
            }
        });

        return command;
    }

    private static void WriteCompact(FileDependencyGraph graph, List<int[]> sccs, bool suggest, int top)
    {
        int n = graph.Files.Count;
        if (sccs.Count == 0)
        {
            Console.WriteLine($"reforge cycles — 0 non-trivial SCCs in {n} files");
            Console.WriteLine("(codebase is acyclic at file level)");
            return;
        }

        Console.WriteLine($"reforge cycles — {sccs.Count} non-trivial SCCs in {n} files");
        Console.WriteLine();

        int shown = suggest ? 1 : Math.Min(sccs.Count, 10);
        for (int i = 0; i < shown; i++)
        {
            var scc = sccs[i];
            double pct = n > 0 ? (double)scc.Length / n : 0;
            string tag = i == 0 ? "core SCC" : $"SCC #{i + 1}";
            Console.WriteLine($"#{i + 1,-3} {tag} — {scc.Length} files ({pct:P1} of codebase)");

            // Show a few representative members for context (namespace prefix summary).
            var samples = scc.Take(5).Select(idx => "    " + graph.Files[idx]);
            foreach (var s in samples) Console.WriteLine(s);
            if (scc.Length > 5) Console.WriteLine($"    ... and {scc.Length - 5} more");
            Console.WriteLine();
        }

        if (!suggest)
        {
            if (sccs.Count > shown)
                Console.WriteLine($"(showing top {shown} of {sccs.Count}; run with --suggest for refactoring targets)");
            return;
        }

        // Betweenness centrality inside the core SCC.
        var core = sccs[0];
        var bc = StructuralAnalysis.ComputeBetweenness(core, graph.Adj);
        var ranked = bc.OrderByDescending(kv => kv.Value).Take(top).ToList();

        Console.WriteLine($"top {ranked.Count} refactoring targets in core SCC (by betweenness centrality):");
        Console.WriteLine("(files routing the most dependency paths — highest structural payoff to untangle)");
        Console.WriteLine();

        int rank = 1;
        foreach (var (fileIdx, score) in ranked)
        {
            var outgoing = graph.Adj[fileIdx].Where(i => Array.IndexOf(core, i) >= 0).ToArray();
            var incoming = graph.RevAdj[fileIdx].Where(i => Array.IndexOf(core, i) >= 0).ToArray();

            Console.WriteLine($"  {rank}. {graph.Files[fileIdx]}");
            Console.WriteLine($"     betweenness: {score:F1}  fan-out (in core): {outgoing.Length}  fan-in (in core): {incoming.Length}");
            if (outgoing.Length > 0)
            {
                var sample = string.Join(", ", outgoing.Take(3).Select(i => Path.GetFileName(graph.Files[i])));
                Console.WriteLine($"     depends on: {sample}{(outgoing.Length > 3 ? $" (+{outgoing.Length - 3})" : "")}");
            }
            if (incoming.Length > 0)
            {
                var sample = string.Join(", ", incoming.Take(3).Select(i => Path.GetFileName(graph.Files[i])));
                Console.WriteLine($"     depended on by: {sample}{(incoming.Length > 3 ? $" (+{incoming.Length - 3})" : "")}");
            }
            Console.WriteLine();
            rank++;
        }
    }

    private static void WriteJson(FileDependencyGraph graph, List<int[]> sccs, bool suggest, int top)
    {
        var output = new
        {
            command = "cycles",
            totalFiles = graph.Files.Count,
            cycleCount = sccs.Count,
            sccs = sccs.Select((scc, i) => new
            {
                rank = i + 1,
                size = scc.Length,
                pctOfCodebase = graph.Files.Count > 0 ? (double)scc.Length / graph.Files.Count : 0,
                files = scc.Select(idx => graph.Files[idx]).ToArray()
            }).ToArray(),
            suggestions = suggest && sccs.Count > 0
                ? StructuralAnalysis.ComputeBetweenness(sccs[0], graph.Adj)
                    .OrderByDescending(kv => kv.Value)
                    .Take(top)
                    .Select(kv => new
                    {
                        file = graph.Files[kv.Key],
                        betweenness = Math.Round(kv.Value, 2),
                        fanOutInCore = graph.Adj[kv.Key].Count(i => Array.IndexOf(sccs[0], i) >= 0),
                        fanInInCore = graph.RevAdj[kv.Key].Count(i => Array.IndexOf(sccs[0], i) >= 0)
                    }).ToArray()
                : null
        };

        Console.WriteLine(JsonSerializer.Serialize(output, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        }));
    }
}
