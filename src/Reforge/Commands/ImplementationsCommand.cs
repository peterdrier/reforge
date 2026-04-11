using System.CommandLine;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;

namespace Reforge.Commands;

public static class ImplementationsCommand
{
    public static Command Create(Option<string?> solutionOption, Option<OutputFormat> formatOption)
    {
        var interfaceArg = new Argument<string>("interface") { Description = "The interface or abstract class to find implementations of" };
        var command = new Command("implementations", "Find all types implementing an interface or abstract class") { interfaceArg };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var solutionPath = parseResult.GetValue(solutionOption);
            var format = parseResult.GetValue(formatOption);
            var symbolQuery = parseResult.GetValue(interfaceArg)!;

            var (solution, workspace) = await WorkspaceHelper.OpenSolutionAsync(solutionPath);
            using (workspace)
            {
                var symbols = await SymbolResolver.ResolveAsync(solution, symbolQuery);
                if (symbols.Count == 0)
                {
                    var suggestions = await SymbolResolver.SuggestAsync(solution, symbolQuery);
                    var msg = suggestions.Count > 0
                        ? $"Symbol '{symbolQuery}' not found. Did you mean: {string.Join(", ", suggestions)}"
                        : $"Symbol '{symbolQuery}' not found.";
                    OutputFormatter.WriteMessage("implementations", msg, format);
                    return;
                }

                if (symbols.Count > 1)
                {
                    var candidates = string.Join(", ", symbols.Select(s => s.ToDisplayString()));
                    OutputFormatter.WriteMessage("implementations",
                        $"Ambiguous symbol '{symbolQuery}'. Candidates: {candidates}", format);
                    return;
                }

                var symbol = symbols[0];
                if (symbol is not INamedTypeSymbol typeSymbol)
                {
                    OutputFormatter.WriteMessage("implementations",
                        $"Symbol '{symbolQuery}' is not a type (it is a {symbol.Kind}).", format);
                    return;
                }

                var solutionDir = LocationHelper.GetSolutionDirectory(solution);

                var implementations = await SymbolFinder.FindImplementationsAsync(
                    typeSymbol, solution, cancellationToken: cancellationToken);

                // Each implementation is an ISymbol — get its primary declaration location
                var implList = implementations
                    .Where(impl => impl.Locations.Length > 0 && impl.Locations[0].IsInSource)
                    .ToList();

                OutputFormatter.WriteResults(
                    "implementations",
                    typeSymbol.ToDisplayString(),
                    implList,
                    format,
                    impl => LocationHelper.ToResultEntry(impl.Locations[0], impl, solutionDir));
            }
        });

        return command;
    }
}
