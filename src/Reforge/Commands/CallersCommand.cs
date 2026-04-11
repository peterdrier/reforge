using System.CommandLine;
using System.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;

namespace Reforge.Commands;

public static class CallersCommand
{
    public static Command Create(Option<string?> solutionOption, Option<OutputFormat> formatOption, Option<int?> limitOption)
    {
        var methodArg = new Argument<string>("method") { Description = "The method to find callers of" };
        var command = new Command("callers", "Find all callers of a method") { methodArg };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var sw = Stopwatch.StartNew();
            var solutionPath = parseResult.GetValue(solutionOption);
            var format = parseResult.GetValue(formatOption);
            var symbolQuery = parseResult.GetValue(methodArg)!;
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
                    OutputFormatter.WriteMessage("callers", msg, format);
                    sw.Stop();
                    Telemetry.Log("callers", symbolQuery, 0, sw.ElapsedMilliseconds);
                    return;
                }

                if (symbols.Count > 1)
                {
                    var candidates = string.Join(", ", symbols.Select(s => s.ToDisplayString()));
                    OutputFormatter.WriteMessage("callers",
                        $"Ambiguous symbol '{symbolQuery}'. Candidates: {candidates}", format);
                    sw.Stop();
                    Telemetry.Log("callers", $"{symbolQuery} (ambiguous, {symbols.Count} candidates)", 0, sw.ElapsedMilliseconds);
                    return;
                }

                var symbol = symbols[0];
                if (symbol is not IMethodSymbol methodSymbol)
                {
                    OutputFormatter.WriteMessage("callers",
                        $"Symbol '{symbolQuery}' is not a method (it is a {symbol.Kind}).", format);
                    sw.Stop();
                    Telemetry.Log("callers", symbolQuery, 0, sw.ElapsedMilliseconds);
                    return;
                }

                var solutionDir = LocationHelper.GetSolutionDirectory(solution);

                var callers = await SymbolFinder.FindCallersAsync(methodSymbol, solution, cancellationToken);
                var callerLocations = callers
                    .SelectMany(c => c.Locations.Select(loc => (c.CallingSymbol, Location: loc)))
                    .ToList();

                int? totalBeforeLimit = null;
                if (limit.HasValue && callerLocations.Count > limit.Value)
                {
                    totalBeforeLimit = callerLocations.Count;
                    callerLocations = callerLocations.Take(limit.Value).ToList();
                }

                OutputFormatter.WriteResults(
                    "callers",
                    methodSymbol.ToDisplayString(),
                    callerLocations,
                    format,
                    entry => LocationHelper.ToResultEntry(entry.Location, entry.CallingSymbol, solutionDir),
                    totalBeforeLimit);

                sw.Stop();
                Telemetry.Log("callers", symbolQuery, totalBeforeLimit ?? callerLocations.Count, sw.ElapsedMilliseconds);
            }
        });

        return command;
    }
}
