using System.CommandLine;
using System.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;

namespace Reforge.Commands;

public static class InheritorsCommand
{
    public static Command Create(Option<string?> solutionOption, Option<OutputFormat> formatOption, Option<int?> limitOption)
    {
        var symbolArg = new Argument<string>("type") { Description = "The base type or interface to find inheritors of" };
        var command = new Command("inheritors", "Find all types that derive from a base type or implement an interface") { symbolArg };

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
                    OutputFormatter.WriteMessage("inheritors", msg, format);
                    sw.Stop();
                    Telemetry.Log("inheritors", symbolQuery, 0, sw.ElapsedMilliseconds);
                    return;
                }

                if (symbols.Count > 1)
                {
                    var candidates = string.Join(", ", symbols.Select(s => s.ToDisplayString()));
                    OutputFormatter.WriteMessage("inheritors",
                        $"Ambiguous symbol '{symbolQuery}'. Candidates: {candidates}", format);
                    sw.Stop();
                    Telemetry.Log("inheritors", $"{symbolQuery} (ambiguous, {symbols.Count} candidates)", 0, sw.ElapsedMilliseconds);
                    return;
                }

                if (symbols[0] is not INamedTypeSymbol typeSymbol)
                {
                    OutputFormatter.WriteMessage("inheritors",
                        $"'{symbolQuery}' is not a type (it is a {symbols[0].Kind}).", format);
                    sw.Stop();
                    Telemetry.Log("inheritors", symbolQuery, 0, sw.ElapsedMilliseconds);
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

                int? totalBeforeLimit = null;
                if (limit.HasValue && entries.Count > limit.Value)
                {
                    totalBeforeLimit = entries.Count;
                    entries = entries.Take(limit.Value).ToList();
                }

                OutputFormatter.WriteResults(
                    "inheritors",
                    typeSymbol.ToDisplayString(),
                    entries,
                    format,
                    entry => entry,
                    totalBeforeLimit);

                sw.Stop();
                Telemetry.Log("inheritors", symbolQuery, totalBeforeLimit ?? entries.Count, sw.ElapsedMilliseconds);
            }
        });

        return command;
    }
}
