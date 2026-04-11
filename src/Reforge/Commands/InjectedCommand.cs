using System.CommandLine;
using Microsoft.CodeAnalysis;

namespace Reforge.Commands;

public static class InjectedCommand
{
    public static Command Create(Option<string?> solutionOption, Option<OutputFormat> formatOption)
    {
        var symbolArg = new Argument<string>("type") { Description = "The type to find injection sites for" };
        var command = new Command("injected", "Find all classes that inject a given type via constructor") { symbolArg };

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
                    OutputFormatter.WriteMessage("injected", msg, format);
                    return;
                }

                if (symbols.Count > 1)
                {
                    var candidates = string.Join(", ", symbols.Select(s => s.ToDisplayString()));
                    OutputFormatter.WriteMessage("injected",
                        $"Ambiguous symbol '{symbolQuery}'. Candidates: {candidates}", format);
                    return;
                }

                if (symbols[0] is not INamedTypeSymbol targetSymbol)
                {
                    OutputFormatter.WriteMessage("injected",
                        $"'{symbolQuery}' is not a type (it is a {symbols[0].Kind}).", format);
                    return;
                }

                var solutionDir = LocationHelper.GetSolutionDirectory(solution);
                var targetDisplayName = targetSymbol.ToDisplayString();
                var entries = new List<ResultEntry>();

                // Walk all types in every project looking for constructor params matching the target type
                foreach (var project in solution.Projects)
                {
                    var compilation = await project.GetCompilationAsync(cancellationToken);
                    if (compilation is null)
                        continue;

                    foreach (var type in GetAllTypes(compilation.GlobalNamespace))
                    {
                        foreach (var ctor in type.Constructors)
                        {
                            if (ctor.IsImplicitlyDeclared)
                                continue;

                            foreach (var param in ctor.Parameters)
                            {
                                // Match by fully qualified name since symbols come from different compilations
                                if (param.Type.ToDisplayString() == targetDisplayName)
                                {
                                    var location = ctor.Locations.FirstOrDefault(l => l.IsInSource);
                                    if (location is not null)
                                    {
                                        var lineSpan = location.GetLineSpan();
                                        var filePath = LocationHelper.NormalizePath(lineSpan.Path, solutionDir);
                                        var line = lineSpan.StartLinePosition.Line + 1;
                                        var column = lineSpan.StartLinePosition.Character + 1;

                                        entries.Add(new ResultEntry(
                                            filePath,
                                            line,
                                            column,
                                            $"{type.Name}({param.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)} {param.Name})",
                                            type.Name));
                                    }
                                }
                            }
                        }
                    }
                }

                // Deduplicate entries that may appear from multiple compilations
                var deduped = entries
                    .GroupBy(e => $"{e.File}:{e.Line}:{e.Column}")
                    .Select(g => g.First())
                    .ToList();

                OutputFormatter.WriteResults(
                    "injected",
                    targetSymbol.ToDisplayString(),
                    deduped,
                    format,
                    entry => entry);
            }
        });

        return command;
    }

    /// <summary>
    /// Recursively walks all namespaces and collects every named type symbol.
    /// </summary>
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
                    // Also yield nested types
                    foreach (var nested in GetNestedTypes(type))
                        yield return nested;
                    break;
            }
        }
    }

    private static IEnumerable<INamedTypeSymbol> GetNestedTypes(INamedTypeSymbol type)
    {
        foreach (var nested in type.GetTypeMembers())
        {
            yield return nested;
            foreach (var deepNested in GetNestedTypes(nested))
                yield return deepNested;
        }
    }
}
