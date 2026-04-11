using System.CommandLine;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;

namespace Reforge.Commands;

public static class CallersCommand
{
    public static Command Create(Option<string?> solutionOption, Option<OutputFormat> formatOption)
    {
        var methodArg = new Argument<string>("method") { Description = "The method to find callers of" };
        var command = new Command("callers", "Find all callers of a method") { methodArg };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var solutionPath = parseResult.GetValue(solutionOption);
            var format = parseResult.GetValue(formatOption);
            var symbolQuery = parseResult.GetValue(methodArg)!;

            var (solution, workspace) = await WorkspaceHelper.OpenSolutionAsync(solutionPath);
            using (workspace)
            {
                var symbols = await SymbolResolver.ResolveAsync(solution, symbolQuery);
                if (symbols.Count == 0)
                {
                    OutputFormatter.WriteMessage("callers", $"Symbol '{symbolQuery}' not found.", format);
                    return;
                }

                if (symbols.Count > 1)
                {
                    var candidates = string.Join(", ", symbols.Select(s => s.ToDisplayString()));
                    OutputFormatter.WriteMessage("callers",
                        $"Ambiguous symbol '{symbolQuery}'. Candidates: {candidates}", format);
                    return;
                }

                var symbol = symbols[0];
                if (symbol is not IMethodSymbol methodSymbol)
                {
                    OutputFormatter.WriteMessage("callers",
                        $"Symbol '{symbolQuery}' is not a method (it is a {symbol.Kind}).", format);
                    return;
                }

                var solutionDir = LocationHelper.GetSolutionDirectory(solution);

                var callers = await SymbolFinder.FindCallersAsync(methodSymbol, solution, cancellationToken);
                var callerLocations = callers
                    .SelectMany(c => c.Locations.Select(loc => (c.CallingSymbol, Location: loc)))
                    .ToList();

                OutputFormatter.WriteResults(
                    "callers",
                    methodSymbol.ToDisplayString(),
                    callerLocations,
                    format,
                    entry => LocationHelper.ToResultEntry(entry.Location, entry.CallingSymbol, solutionDir));
            }
        });

        return command;
    }
}
