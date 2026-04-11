using System.CommandLine;
using System.Diagnostics;
using Microsoft.CodeAnalysis;

namespace Reforge.Commands;

public static class InjectedCommand
{
    public static Command Create(Option<string?> solutionOption, Option<OutputFormat> formatOption, Option<int?> limitOption)
    {
        var symbolArg = new Argument<string>("type") { Description = "The type to find injection sites for" };
        var command = new Command("injected", "Find all classes that inject a given type via constructor") { symbolArg };

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
                    OutputFormatter.WriteMessage("injected", msg, format);
                    sw.Stop();
                    Telemetry.Log("injected", symbolQuery, 0, sw.ElapsedMilliseconds);
                    return;
                }

                if (symbols.Count > 1)
                {
                    var candidates = string.Join(", ", symbols.Select(s => s.ToDisplayString()));
                    OutputFormatter.WriteMessage("injected",
                        $"Ambiguous symbol '{symbolQuery}'. Candidates: {candidates}", format);
                    sw.Stop();
                    Telemetry.Log("injected", $"{symbolQuery} (ambiguous, {symbols.Count} candidates)", 0, sw.ElapsedMilliseconds);
                    return;
                }

                if (symbols[0] is not INamedTypeSymbol targetSymbol)
                {
                    OutputFormatter.WriteMessage("injected",
                        $"'{symbolQuery}' is not a type (it is a {symbols[0].Kind}).", format);
                    sw.Stop();
                    Telemetry.Log("injected", symbolQuery, 0, sw.ElapsedMilliseconds);
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

                int? totalBeforeLimit = null;
                if (limit.HasValue && deduped.Count > limit.Value)
                {
                    totalBeforeLimit = deduped.Count;
                    deduped = deduped.Take(limit.Value).ToList();
                }

                OutputFormatter.WriteResults(
                    "injected",
                    targetSymbol.ToDisplayString(),
                    deduped,
                    format,
                    entry => entry,
                    totalBeforeLimit);

                sw.Stop();
                Telemetry.Log("injected", symbolQuery, totalBeforeLimit ?? deduped.Count, sw.ElapsedMilliseconds);
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
