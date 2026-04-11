using System.CommandLine;
using Microsoft.CodeAnalysis;

namespace Reforge.Commands;

public static class ParametersCommand
{
    public static Command Create(Option<string?> solutionOption, Option<OutputFormat> formatOption)
    {
        var nameOption = new Option<string?>("--name")
        {
            Description = "Substring match against parameter names (case-insensitive)"
        };
        var typeOption = new Option<string?>("--type")
        {
            Description = "Substring match against parameter type names (case-insensitive)"
        };

        var command = new Command("parameters", "Find method parameters matching name and/or type criteria")
        {
            nameOption,
            typeOption
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var solutionPath = parseResult.GetValue(solutionOption);
            var format = parseResult.GetValue(formatOption);
            var namePattern = parseResult.GetValue(nameOption);
            var typePattern = parseResult.GetValue(typeOption);

            if (namePattern is null && typePattern is null)
            {
                OutputFormatter.WriteMessage("parameters",
                    "At least one of --name or --type must be provided.", format);
                return;
            }

            var (solution, handle) = await WorkspaceHelper.OpenSolutionAsync(solutionPath);
            using (handle)
            {
                var solutionDir = LocationHelper.GetSolutionDirectory(solution);
                var entries = new List<ResultEntry>();

                foreach (var project in solution.Projects)
                {
                    var compilation = await project.GetCompilationAsync(cancellationToken);
                    if (compilation is null)
                        continue;

                    foreach (var type in GetAllTypes(compilation.GlobalNamespace))
                    {
                        foreach (var member in type.GetMembers().OfType<IMethodSymbol>())
                        {
                            if (member.IsImplicitlyDeclared)
                                continue;

                            foreach (var param in member.Parameters)
                            {
                                bool nameMatch = namePattern is null ||
                                    param.Name.Contains(namePattern, StringComparison.OrdinalIgnoreCase);
                                bool typeMatch = typePattern is null ||
                                    param.Type.ToDisplayString().Contains(typePattern, StringComparison.OrdinalIgnoreCase);

                                if (nameMatch && typeMatch)
                                {
                                    var location = param.Locations.FirstOrDefault(l => l.IsInSource)
                                                ?? member.Locations.FirstOrDefault(l => l.IsInSource);

                                    if (location is null)
                                        continue;

                                    var lineSpan = location.GetLineSpan();
                                    var filePath = LocationHelper.NormalizePath(lineSpan.Path, solutionDir);
                                    var line = lineSpan.StartLinePosition.Line + 1;
                                    var column = lineSpan.StartLinePosition.Character + 1;

                                    entries.Add(new ResultEntry(
                                        filePath,
                                        line,
                                        column,
                                        $"{member.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)} — parameter: {param.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)} {param.Name}",
                                        $"{type.Name}.{member.Name}"));
                                }
                            }
                        }
                    }
                }

                // Deduplicate entries that may appear from multiple compilations
                var deduped = entries
                    .GroupBy(r => (r.File, r.Line, r.Column))
                    .Select(g => g.First())
                    .ToList();

                var symbolDesc = BuildSymbolDescription(namePattern, typePattern);
                OutputFormatter.WriteResults(
                    "parameters",
                    symbolDesc,
                    deduped,
                    format,
                    entry => entry);
            }
        });

        return command;
    }

    private static IEnumerable<INamedTypeSymbol> GetAllTypes(INamespaceSymbol ns)
    {
        foreach (var member in ns.GetMembers())
        {
            if (member is INamespaceSymbol childNs)
            {
                foreach (var t in GetAllTypes(childNs))
                    yield return t;
            }
            else if (member is INamedTypeSymbol type)
            {
                yield return type;
                foreach (var nested in type.GetTypeMembers())
                    yield return nested;
            }
        }
    }

    private static string BuildSymbolDescription(string? namePattern, string? typePattern)
    {
        var parts = new List<string>();
        if (namePattern is not null)
            parts.Add($"name~'{namePattern}'");
        if (typePattern is not null)
            parts.Add($"type~'{typePattern}'");
        return string.Join(" && ", parts);
    }
}
