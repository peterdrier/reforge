using Microsoft.CodeAnalysis;

namespace Reforge;

public static class SymbolResolver
{
    /// <summary>
    /// Resolves a symbol string against the solution's semantic model.
    /// Supports:
    ///   - Simple name: "User" (matches symbol.Name)
    ///   - Qualified name: "Core.Models.User" (matches namespace path)
    ///   - Member access: "UserService.GetUserAsync" (resolves type, then finds member)
    /// Returns all matches. Caller decides whether to error on ambiguity or use all.
    /// </summary>
    public static async Task<IReadOnlyList<ISymbol>> ResolveAsync(Solution solution, string symbolQuery)
    {
        if (string.IsNullOrWhiteSpace(symbolQuery))
            return [];

        var allSymbols = await CollectAllSymbolsAsync(solution);

        // Try member access first: "Type.Member"
        var dotIndex = symbolQuery.LastIndexOf('.');
        if (dotIndex > 0)
        {
            var typePart = symbolQuery[..dotIndex];
            var memberPart = symbolQuery[(dotIndex + 1)..];

            // Try as Type.Member
            var typeMatches = MatchSymbols(allSymbols, typePart);
            var memberResults = new List<ISymbol>();
            foreach (var type in typeMatches.OfType<INamedTypeSymbol>())
            {
                memberResults.AddRange(
                    type.GetMembers(memberPart)
                        .Where(m => m.CanBeReferencedByName));
            }

            if (memberResults.Count > 0)
                return Deduplicate(memberResults);

            // Not a Type.Member — try as a qualified name
            var qualifiedMatches = MatchSymbols(allSymbols, symbolQuery);
            if (qualifiedMatches.Count > 0)
                return qualifiedMatches;
        }

        // Simple name lookup
        return MatchSymbols(allSymbols, symbolQuery);
    }

    /// <summary>
    /// Suggests possible symbols when a query yields no results.
    /// Finds named types whose name contains the query as a substring (case-insensitive).
    /// Returns up to 10 fully qualified display names.
    /// </summary>
    public static async Task<IReadOnlyList<string>> SuggestAsync(Solution solution, string query)
    {
        var allSymbols = await CollectAllSymbolsAsync(solution);
        return allSymbols
            .OfType<INamedTypeSymbol>()
            .Where(s => s.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                      && s.Locations.Any(l => l.IsInSource))
            .Select(s => s.ToDisplayString())
            .Distinct()
            .Take(10)
            .ToList();
    }

    /// <summary>
    /// Collects all named type symbols from every project in the solution.
    /// Uses GetSymbolsWithName for efficiency where possible.
    /// </summary>
    private static async Task<List<ISymbol>> CollectAllSymbolsAsync(Solution solution)
    {
        var results = new List<ISymbol>();
        var seen = new HashSet<ISymbol>(SymbolEqualityComparer.Default);

        foreach (var project in solution.Projects)
        {
            var compilation = await project.GetCompilationAsync();
            if (compilation is null)
                continue;

            CollectNamespaceMembers(compilation.GlobalNamespace, results, seen);
        }

        return results;
    }

    /// <summary>
    /// Recursively walks all types and nested namespaces, collecting named types.
    /// </summary>
    private static void CollectNamespaceMembers(
        INamespaceSymbol ns,
        List<ISymbol> results,
        HashSet<ISymbol> seen)
    {
        foreach (var member in ns.GetMembers())
        {
            switch (member)
            {
                case INamespaceSymbol childNs:
                    CollectNamespaceMembers(childNs, results, seen);
                    break;

                case INamedTypeSymbol type when seen.Add(type):
                    results.Add(type);
                    // Also collect members so Type.Member resolution works
                    foreach (var m in type.GetMembers())
                    {
                        if (m.CanBeReferencedByName && seen.Add(m))
                            results.Add(m);
                    }
                    break;
            }
        }
    }

    /// <summary>
    /// Matches collected symbols against a query string.
    /// If the query contains '.', tries qualified name matching.
    /// Otherwise matches by simple Name.
    /// </summary>
    private static IReadOnlyList<ISymbol> MatchSymbols(List<ISymbol> allSymbols, string query)
    {
        if (query.Contains('.'))
        {
            // Qualified name: match against ToDisplayString or ends-with on the qualified name
            var exact = allSymbols
                .Where(s => s.ToDisplayString() == query)
                .ToList();
            if (exact.Count > 0)
                return Deduplicate(exact);

            // Partial qualified: match if the display string ends with the query
            var partial = allSymbols
                .Where(s => s.ToDisplayString().EndsWith("." + query, StringComparison.Ordinal)
                         || s.ToDisplayString() == query)
                .ToList();
            return Deduplicate(partial);
        }

        // Simple name match
        var matches = allSymbols
            .Where(s => s.Name == query)
            .ToList();
        return Deduplicate(matches);
    }

    /// <summary>
    /// Deduplicates symbols that appear in multiple project compilations.
    /// </summary>
    private static IReadOnlyList<ISymbol> Deduplicate(List<ISymbol> symbols)
    {
        var seen = new HashSet<string>();
        var result = new List<ISymbol>();
        foreach (var s in symbols)
        {
            // Use display string + kind as dedup key since SymbolEqualityComparer
            // won't match across different compilation instances
            var key = $"{s.Kind}:{s.ToDisplayString()}";
            if (seen.Add(key))
                result.Add(s);
        }
        return result;
    }
}
