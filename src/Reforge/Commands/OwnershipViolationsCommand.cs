using System.CommandLine;
using System.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Reforge.Commands;

public static class OwnershipViolationsCommand
{
    public static Command Create(Option<string?> solutionOption, Option<OutputFormat> formatOption, Option<int?> limitOption)
    {
        var ownerOption = new Option<string>("--owner") { Description = "The service that owns these tables" };
        ownerOption.Required = true;
        var tablesOption = new Option<string>("--tables") { Description = "Comma-separated DbSet property names" };
        tablesOption.Required = true;

        var command = new Command("ownership-violations", "Find services that access tables they don't own")
        {
            ownerOption, tablesOption
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var sw = Stopwatch.StartNew();
            var solutionPath = parseResult.GetValue(solutionOption);
            var format = parseResult.GetValue(formatOption);
            var limit = parseResult.GetValue(limitOption);
            var ownerName = parseResult.GetValue(ownerOption)!;
            var tablesRaw = parseResult.GetValue(tablesOption)!;

            var tableNames = tablesRaw
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet(StringComparer.Ordinal);

            var (solution, handle) = await WorkspaceHelper.OpenSolutionAsync(solutionPath);
            using (handle)
            {
                var solutionDir = LocationHelper.GetSolutionDirectory(solution);
                var entries = new List<ResultEntry>();

                // Walk all types in every project
                foreach (var project in solution.Projects)
                {
                    // Skip test projects
                    if (project.Name.Contains("Test", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var compilation = await project.GetCompilationAsync(cancellationToken);
                    if (compilation is null)
                        continue;

                    foreach (var type in GetAllTypes(compilation.GlobalNamespace))
                    {
                        // Skip types not in source
                        if (!type.Locations.Any(l => l.IsInSource))
                            continue;

                        // Skip the owner class
                        if (type.Name == ownerName || type.ToDisplayString().EndsWith("." + ownerName))
                            continue;

                        // Find DbSet accesses in this type
                        var accesses = await DbContextAnalyzer.FindDbSetAccessesAsync(type, solution, cancellationToken);

                        foreach (var access in accesses)
                        {
                            if (tableNames.Contains(access.DbSetName))
                            {
                                var lineSpan = access.Location.GetLineSpan();
                                var filePath = LocationHelper.NormalizePath(lineSpan.Path, solutionDir);
                                var line = lineSpan.StartLinePosition.Line + 1;
                                var column = lineSpan.StartLinePosition.Character + 1;
                                entries.Add(new ResultEntry(filePath, line, column, access.SourceLine, type.Name));
                            }
                        }
                    }
                }

                // Dedup entries that may appear from multiple compilations
                var deduped = entries
                    .GroupBy(e => $"{e.File}:{e.Line}:{e.Column}")
                    .Select(g => g.First())
                    .ToList();

                int? totalBeforeLimit = null;
                if (limit.HasValue && deduped.Count > limit.Value)
                {
                    totalBeforeLimit = deduped.Count;
                    deduped = deduped.Take(limit.Value).ToList();
                }

                var symbolDisplay = $"{ownerName} owning {tablesRaw}";
                OutputFormatter.WriteResults(
                    "ownership-violations",
                    symbolDisplay,
                    deduped,
                    format,
                    entry => entry,
                    totalBeforeLimit);

                sw.Stop();
                Telemetry.Log("ownership-violations", $"{ownerName} tables={tablesRaw}", totalBeforeLimit ?? deduped.Count, sw.ElapsedMilliseconds);
            }
        });

        return command;
    }

    private static IEnumerable<INamedTypeSymbol> GetAllTypes(INamespaceSymbol ns)
    {
        foreach (var member in ns.GetMembers())
        {
            switch (member)
            {
                case INamespaceSymbol childNs:
                    foreach (var type in GetAllTypes(childNs))
                        yield return type;
                    break;

                case INamedTypeSymbol type:
                    yield return type;
                    foreach (var nested in type.GetTypeMembers())
                        yield return nested;
                    break;
            }
        }
    }
}
