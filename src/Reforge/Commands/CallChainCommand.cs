using System.CommandLine;
using System.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;

namespace Reforge.Commands;

public static class CallChainCommand
{
    public static Command Create(Option<string?> solutionOption, Option<OutputFormat> formatOption, Option<int?> limitOption)
    {
        var methodArg = new Argument<string>("method") { Description = "The method to find transitive callers of" };
        var depthOption = new Option<int>("--depth")
        {
            Description = "Maximum recursion depth",
            DefaultValueFactory = _ => 5
        };

        var command = new Command("call-chain", "Find transitive callers of a method (who calls who calls this)")
        {
            methodArg,
            depthOption
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var sw = Stopwatch.StartNew();
            var solutionPath = parseResult.GetValue(solutionOption);
            var format = parseResult.GetValue(formatOption);
            var symbolQuery = parseResult.GetValue(methodArg)!;
            var maxDepth = parseResult.GetValue(depthOption);
            var limit = parseResult.GetValue(limitOption);

            var (solution, handle) = await WorkspaceHelper.OpenSolutionAsync(solutionPath);
            using (handle)
            {
                var symbols = await SymbolResolver.ResolveAsync(solution, symbolQuery);
                if (symbols.Count == 0)
                {
                    var suggestions = await SymbolResolver.SuggestAsync(solution, symbolQuery);
                    var msg = suggestions.Count > 0
                        ? $"Symbol '{symbolQuery}' not found. Did you mean: {string.Join(", ", suggestions)}"
                        : $"Symbol '{symbolQuery}' not found.";
                    OutputFormatter.WriteMessage("call-chain", msg, format);
                    sw.Stop();
                    Telemetry.Log("call-chain", symbolQuery, 0, sw.ElapsedMilliseconds);
                    return;
                }

                if (symbols.Count > 1)
                {
                    var candidates = string.Join(", ", symbols.Select(s => s.ToDisplayString()));
                    OutputFormatter.WriteMessage("call-chain",
                        $"Ambiguous symbol '{symbolQuery}'. Candidates: {candidates}", format);
                    sw.Stop();
                    Telemetry.Log("call-chain", $"{symbolQuery} (ambiguous, {symbols.Count} candidates)", 0, sw.ElapsedMilliseconds);
                    return;
                }

                var symbol = symbols[0];
                if (symbol is not IMethodSymbol methodSymbol)
                {
                    OutputFormatter.WriteMessage("call-chain",
                        $"Symbol '{symbolQuery}' is not a method (it is a {symbol.Kind}).", format);
                    sw.Stop();
                    Telemetry.Log("call-chain", symbolQuery, 0, sw.ElapsedMilliseconds);
                    return;
                }

                var solutionDir = LocationHelper.GetSolutionDirectory(solution);
                var chain = await FindCallChainAsync(methodSymbol, solution, maxDepth, cancellationToken);

                int? totalBeforeLimit = null;
                if (limit.HasValue && chain.Count > limit.Value)
                {
                    totalBeforeLimit = chain.Count;
                    chain = chain.Take(limit.Value).ToList();
                }

                OutputFormatter.WriteResults(
                    "call-chain",
                    methodSymbol.ToDisplayString(),
                    chain,
                    format,
                    entry => LocationHelper.ToResultEntry(entry.Location, entry.Caller, solutionDir) with
                    {
                        Context = $"[depth {entry.Depth}] {entry.Caller.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}"
                    },
                    totalBeforeLimit);

                sw.Stop();
                Telemetry.Log("call-chain", symbolQuery, totalBeforeLimit ?? chain.Count, sw.ElapsedMilliseconds);
            }
        });

        return command;
    }

    private static async Task<List<(ISymbol Caller, int Depth, Location Location)>> FindCallChainAsync(
        ISymbol method, Solution solution, int maxDepth, CancellationToken ct)
    {
        var results = new List<(ISymbol Caller, int Depth, Location Location)>();
        var visited = new HashSet<string>();
        var queue = new Queue<(ISymbol method, int depth)>();

        queue.Enqueue((method, 0));
        visited.Add(method.ToDisplayString());

        while (queue.Count > 0)
        {
            var (current, depth) = queue.Dequeue();
            if (depth >= maxDepth)
                continue;

            var callers = await SymbolFinder.FindCallersAsync(current, solution, ct);
            foreach (var caller in callers)
            {
                var key = caller.CallingSymbol.ToDisplayString();
                if (visited.Add(key))
                {
                    var loc = caller.Locations.FirstOrDefault();
                    if (loc != null)
                        results.Add((caller.CallingSymbol, depth + 1, loc));

                    queue.Enqueue((caller.CallingSymbol, depth + 1));
                }
            }
        }

        return results;
    }
}
