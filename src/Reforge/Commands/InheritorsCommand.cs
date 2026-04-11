using System.CommandLine;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;

namespace Reforge.Commands;

public static class InheritorsCommand
{
    public static Command Create(Option<string?> solutionOption, Option<OutputFormat> formatOption)
    {
        var symbolArg = new Argument<string>("type") { Description = "The base type or interface to find inheritors of" };
        var command = new Command("inheritors", "Find all types that derive from a base type or implement an interface") { symbolArg };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var solutionPath = parseResult.GetValue(solutionOption);
            var format = parseResult.GetValue(formatOption);
            var symbolQuery = parseResult.GetValue(symbolArg)!;

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
                    OutputFormatter.WriteMessage("inheritors", msg, format);
                    return;
                }

                if (symbols.Count > 1)
                {
                    var candidates = string.Join(", ", symbols.Select(s => s.ToDisplayString()));
                    OutputFormatter.WriteMessage("inheritors",
                        $"Ambiguous symbol '{symbolQuery}'. Candidates: {candidates}", format);
                    return;
                }

                if (symbols[0] is not INamedTypeSymbol typeSymbol)
                {
                    OutputFormatter.WriteMessage("inheritors",
                        $"'{symbolQuery}' is not a type (it is a {symbols[0].Kind}).", format);
                    return;
                }

                var solutionDir = LocationHelper.GetSolutionDirectory(solution);

                // Use the appropriate SymbolFinder API based on whether this is an interface or class
                List<INamedTypeSymbol> derivedTypes;
                if (typeSymbol.TypeKind == TypeKind.Interface)
                {
                    var implementations = await SymbolFinder.FindImplementationsAsync(
                        typeSymbol, solution, cancellationToken: cancellationToken);
                    derivedTypes = implementations.OfType<INamedTypeSymbol>().ToList();
                }
                else
                {
                    var derived = await SymbolFinder.FindDerivedClassesAsync(
                        typeSymbol, solution, cancellationToken: cancellationToken);
                    derivedTypes = derived.ToList();
                }

                // Build result entries from the declaration locations of each derived type
                var entries = new List<ResultEntry>();
                foreach (var derived in derivedTypes)
                {
                    var location = derived.Locations.FirstOrDefault(l => l.IsInSource);
                    if (location is not null)
                    {
                        entries.Add(LocationHelper.ToResultEntry(location, derived, solutionDir));
                    }
                }

                OutputFormatter.WriteResults(
                    "inheritors",
                    typeSymbol.ToDisplayString(),
                    entries,
                    format,
                    entry => entry);
            }
        });

        return command;
    }
}
