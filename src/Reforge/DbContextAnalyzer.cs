using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Reforge;

/// <summary>
/// Shared analysis logic for DbContext field/property accesses.
/// Used by dbset-usage, ownership-violations, and service-map commands.
/// </summary>
public static class DbContextAnalyzer
{
    /// <summary>
    /// Finds all DbSet property accesses within a class through its DbContext fields/properties.
    /// Returns (DbSetPropertyName, Location, SourceLineText) tuples.
    /// </summary>
    public static async Task<List<(string DbSetName, Location Location, string SourceLine)>> FindDbSetAccessesAsync(
        INamedTypeSymbol typeSymbol, Solution solution, CancellationToken ct)
    {
        // Find DbContext fields and properties in this class
        var dbContextNames = new HashSet<string>();

        foreach (var member in typeSymbol.GetMembers())
        {
            switch (member)
            {
                case IFieldSymbol field when IsDbContextType(field.Type):
                    dbContextNames.Add(field.Name);
                    break;
                case IPropertySymbol prop when IsDbContextType(prop.Type):
                    dbContextNames.Add(prop.Name);
                    break;
            }
        }

        var results = new List<(string DbSetName, Location Location, string SourceLine)>();

        if (dbContextNames.Count == 0)
            return results;

        // Find all member accesses on these fields in the class's source
        foreach (var location in typeSymbol.Locations.Where(l => l.IsInSource))
        {
            var tree = location.SourceTree!;
            var root = await tree.GetRootAsync(ct);
            var project = solution.Projects
                .FirstOrDefault(p => p.Documents.Any(d => d.FilePath == tree.FilePath));
            if (project is null)
                continue;

            var compilation = await project.GetCompilationAsync(ct);
            if (compilation is null)
                continue;

            var semanticModel = compilation.GetSemanticModel(tree);
            var classNode = root.FindNode(location.SourceSpan);

            var memberAccesses = classNode.DescendantNodes()
                .OfType<MemberAccessExpressionSyntax>()
                .Where(ma =>
                {
                    // _dbContext.Xxx
                    if (ma.Expression is IdentifierNameSyntax id && dbContextNames.Contains(id.Identifier.Text))
                        return true;
                    // this._dbContext.Xxx
                    if (ma.Expression is MemberAccessExpressionSyntax inner &&
                        inner.Expression is ThisExpressionSyntax &&
                        dbContextNames.Contains(inner.Name.Identifier.Text))
                        return true;
                    return false;
                });

            foreach (var access in memberAccesses)
            {
                var symbolInfo = semanticModel.GetSymbolInfo(access);
                var memberSymbol = symbolInfo.Symbol;
                if (memberSymbol is IPropertySymbol prop && IsDbSetType(prop.Type))
                {
                    var loc = access.GetLocation();
                    var lineSpan = loc.GetLineSpan();
                    var text = loc.SourceTree!.GetText();
                    var sourceLine = text.Lines[lineSpan.StartLinePosition.Line].ToString().Trim();
                    results.Add((prop.Name, loc, sourceLine));
                }
            }
        }

        return results;
    }

    /// <summary>
    /// Checks if a type is or inherits from DbContext (by name convention).
    /// Matches types named "DbContext" or whose name ends with "DbContext",
    /// or any type in their inheritance chain matching that pattern.
    /// </summary>
    public static bool IsDbContextType(ITypeSymbol type)
    {
        var current = type;
        while (current != null)
        {
            if (current.Name == "DbContext" || current.Name.EndsWith("DbContext"))
                return true;
            current = current.BaseType;
        }
        return false;
    }

    /// <summary>
    /// Checks if a type is DbSet&lt;T&gt; (by name).
    /// Also matches List&lt;T&gt; for test solutions that stub DbSet with List.
    /// </summary>
    public static bool IsDbSetType(ITypeSymbol type)
    {
        if (type.Name == "DbSet")
            return true;
        if (type is INamedTypeSymbol named && named.ConstructedFrom?.Name == "DbSet")
            return true;
        return false;
    }

    /// <summary>
    /// Gets all types in the solution that have a DbContext constructor parameter.
    /// These are "services" that consume a DbContext.
    /// Skips test projects and types in bin/obj.
    /// </summary>
    public static async Task<List<(INamedTypeSymbol Type, IParameterSymbol DbContextParam)>> FindDbContextConsumersAsync(
        Solution solution, CancellationToken ct)
    {
        var results = new List<(INamedTypeSymbol, IParameterSymbol)>();
        var seen = new HashSet<string>();

        foreach (var project in solution.Projects)
        {
            // Skip test projects
            if (project.Name.Contains("Test", StringComparison.OrdinalIgnoreCase))
                continue;

            var compilation = await project.GetCompilationAsync(ct);
            if (compilation is null)
                continue;

            foreach (var type in GetAllTypes(compilation.GlobalNamespace))
            {
                // Skip types not defined in source
                if (!type.Locations.Any(l => l.IsInSource))
                    continue;

                // Dedup across projects
                var key = type.ToDisplayString();
                if (!seen.Add(key))
                    continue;

                foreach (var ctor in type.Constructors)
                {
                    if (ctor.IsImplicitlyDeclared)
                        continue;

                    foreach (var param in ctor.Parameters)
                    {
                        if (IsDbContextType(param.Type))
                        {
                            results.Add((type, param));
                            goto nextType; // Only need to find one DbContext param per type
                        }
                    }
                }
                nextType:;
            }
        }

        return results;
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
                    foreach (var nested in type.GetTypeMembers())
                        yield return nested;
                    break;
            }
        }
    }
}
