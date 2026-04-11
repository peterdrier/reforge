using System.CommandLine;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;

namespace Reforge.Commands;

public static class ReferencesCommand
{
    public static Command Create(Option<string?> solutionOption, Option<OutputFormat> formatOption)
    {
        var symbolArg = new Argument<string>("symbol") { Description = "The symbol to find references for" };
        var command = new Command("references", "Find all references to a symbol, solution-wide") { symbolArg };

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
                    OutputFormatter.WriteMessage("references", $"Symbol '{symbolQuery}' not found.", format);
                    return;
                }

                if (symbols.Count > 1)
                {
                    var candidates = string.Join(", ", symbols.Select(s => s.ToDisplayString()));
                    OutputFormatter.WriteMessage("references",
                        $"Ambiguous symbol '{symbolQuery}'. Candidates: {candidates}", format);
                    return;
                }

                var symbol = symbols[0];
                var solutionDir = LocationHelper.GetSolutionDirectory(solution);

                var refs = await SymbolFinder.FindReferencesAsync(symbol, solution, cancellationToken);
                var locations = refs.SelectMany(r => r.Locations).ToList();

                OutputFormatter.WriteResults(
                    "references",
                    symbol.ToDisplayString(),
                    locations,
                    format,
                    loc => LocationHelper.ToResultEntry(loc, solutionDir));
            }
        });

        return command;
    }
}
