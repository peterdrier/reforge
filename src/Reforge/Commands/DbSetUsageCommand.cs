using System.CommandLine;
using System.Diagnostics;
using Microsoft.CodeAnalysis;

namespace Reforge.Commands;

public static class DbSetUsageCommand
{
    public static Command Create(Option<string?> solutionOption, Option<OutputFormat> formatOption, Option<int?> limitOption)
    {
        var symbolArg = new Argument<string>("class") { Description = "The class to analyze for DbSet accesses" };
        var command = new Command("dbset-usage", "Find which DbSet properties a service accesses through its DbContext field") { symbolArg };

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
                    OutputFormatter.WriteMessage("dbset-usage", msg, format);
                    sw.Stop();
                    Telemetry.Log("dbset-usage", symbolQuery, 0, sw.ElapsedMilliseconds);
                    return;
                }

                if (symbols.Count > 1)
                {
                    var candidates = string.Join(", ", symbols.Select(s => s.ToDisplayString()));
                    OutputFormatter.WriteMessage("dbset-usage",
                        $"Ambiguous symbol '{symbolQuery}'. Candidates: {candidates}", format);
                    sw.Stop();
                    Telemetry.Log("dbset-usage", $"{symbolQuery} (ambiguous, {symbols.Count} candidates)", 0, sw.ElapsedMilliseconds);
                    return;
                }

                if (symbols[0] is not INamedTypeSymbol typeSymbol)
                {
                    OutputFormatter.WriteMessage("dbset-usage",
                        $"'{symbolQuery}' is not a type (it is a {symbols[0].Kind}).", format);
                    sw.Stop();
                    Telemetry.Log("dbset-usage", symbolQuery, 0, sw.ElapsedMilliseconds);
                    return;
                }

                var solutionDir = LocationHelper.GetSolutionDirectory(solution);
                var accesses = await DbContextAnalyzer.FindDbSetAccessesAsync(typeSymbol, solution, cancellationToken);

                var entries = accesses.Select(a =>
                {
                    var lineSpan = a.Location.GetLineSpan();
                    var filePath = LocationHelper.NormalizePath(lineSpan.Path, solutionDir);
                    var line = lineSpan.StartLinePosition.Line + 1;
                    var column = lineSpan.StartLinePosition.Character + 1;
                    return new ResultEntry(filePath, line, column, a.SourceLine, typeSymbol.Name);
                }).ToList();

                int? totalBeforeLimit = null;
                if (limit.HasValue && entries.Count > limit.Value)
                {
                    totalBeforeLimit = entries.Count;
                    entries = entries.Take(limit.Value).ToList();
                }

                OutputFormatter.WriteResults(
                    "dbset-usage",
                    typeSymbol.ToDisplayString(),
                    entries,
                    format,
                    entry => entry,
                    totalBeforeLimit);

                sw.Stop();
                Telemetry.Log("dbset-usage", symbolQuery, totalBeforeLimit ?? entries.Count, sw.ElapsedMilliseconds);
            }
        });

        return command;
    }
}
