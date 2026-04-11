using System.CommandLine;
using System.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;

namespace Reforge.Commands;

public static class ReferencesCommand
{
    public static Command Create(Option<string?> solutionOption, Option<OutputFormat> formatOption, Option<int?> limitOption)
    {
        var symbolArg = new Argument<string>("symbol") { Description = "The symbol to find references for" };
        var command = new Command("references", "Find all references to a symbol, solution-wide") { symbolArg };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var sw = Stopwatch.StartNew();
            var solutionPath = parseResult.GetValue(solutionOption);
            var format = parseResult.GetValue(formatOption);
            var symbolQuery = parseResult.GetValue(symbolArg)!;
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
                    OutputFormatter.WriteMessage("references", msg, format);
                    sw.Stop();
                    Telemetry.Log("references", symbolQuery, 0, sw.ElapsedMilliseconds);
                    return;
                }

                if (symbols.Count > 1)
                {
                    var candidates = string.Join(", ", symbols.Select(s => s.ToDisplayString()));
                    OutputFormatter.WriteMessage("references",
                        $"Ambiguous symbol '{symbolQuery}'. Candidates: {candidates}", format);
                    sw.Stop();
                    Telemetry.Log("references", $"{symbolQuery} (ambiguous, {symbols.Count} candidates)", 0, sw.ElapsedMilliseconds);
                    return;
                }

                var symbol = symbols[0];
                var solutionDir = LocationHelper.GetSolutionDirectory(solution);

                var refs = await SymbolFinder.FindReferencesAsync(symbol, solution, cancellationToken);
                var locations = refs.SelectMany(r => r.Locations).ToList();

                int? totalBeforeLimit = null;
                if (limit.HasValue && locations.Count > limit.Value)
                {
                    totalBeforeLimit = locations.Count;
                    locations = locations.Take(limit.Value).ToList();
                }

                OutputFormatter.WriteResults(
                    "references",
                    symbol.ToDisplayString(),
                    locations,
                    format,
                    loc => LocationHelper.ToResultEntry(loc, solutionDir),
                    totalBeforeLimit);

                sw.Stop();
                Telemetry.Log("references", symbolQuery, totalBeforeLimit ?? locations.Count, sw.ElapsedMilliseconds);
            }
        });

        return command;
    }
}
