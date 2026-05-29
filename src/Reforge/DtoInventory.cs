using Microsoft.CodeAnalysis;

namespace Reforge;

/// <summary>
/// Builds a recursive, path-based inventory of a DTO's public readable members. Descends through
/// canonical child DTOs and collection elements (marking collections with "[]"), so the
/// conservation gate can prove facts like CampInfo.Seasons[].Members[].UserId are present.
/// Only canonical/child-DTO types are expanded; primitives and non-canonical types are leaf facts.
/// Depth-bounded and cycle-guarded via a visited-type set on each path.
/// </summary>
public static class DtoInventory
{
    public static IReadOnlyList<string> Build(INamedTypeSymbol root, IReadOnlySet<string> canonicalTypeNames, int maxDepth = 5)
    {
        var paths = new List<string>();
        Walk(root, root.Name, new HashSet<string>(StringComparer.Ordinal) { root.Name }, canonicalTypeNames, paths, maxDepth, 0);
        return paths;
    }

    private static void Walk(INamedTypeSymbol type, string prefix, HashSet<string> visited,
        IReadOnlySet<string> canonical, List<string> paths, int maxDepth, int depth)
    {
        foreach (var prop in type.GetMembers().OfType<IPropertySymbol>())
        {
            if (prop.DeclaredAccessibility != Accessibility.Public) continue;
            if (prop.GetMethod is null) continue;

            var (element, isCollection) = Unwrap(prop.Type);
            string suffix = isCollection ? "[]" : "";
            string path = $"{prefix}.{prop.Name}{suffix}";

            if (element is INamedTypeSymbol named && canonical.Contains(named.Name)
                && !visited.Contains(named.Name) && depth + 1 < maxDepth)
            {
                var nextVisited = new HashSet<string>(visited, StringComparer.Ordinal) { named.Name };
                Walk(named, path, nextVisited, canonical, paths, maxDepth, depth + 1);
            }
            else
            {
                paths.Add(path);
            }
        }
    }

    private static (ITypeSymbol Element, bool IsCollection) Unwrap(ITypeSymbol t)
    {
        if (t is INamedTypeSymbol n && n.IsGenericType && n.TypeArguments.Length == 1)
        {
            var od = n.OriginalDefinition.ToDisplayString();
            if (od.StartsWith("System.Collections.Generic.", StringComparison.Ordinal)
                || od.StartsWith("System.Collections.Immutable.", StringComparison.Ordinal)
                || od == "System.Collections.IEnumerable")
                return (n.TypeArguments[0], true);
        }
        if (t is IArrayTypeSymbol arr) return (arr.ElementType, true);
        return (t, false);
    }
}
