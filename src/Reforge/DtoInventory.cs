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
        foreach (var prop in PublicReadableProperties(type))
        {
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

    /// <summary>
    /// A DTO's public readable instance properties, <b>including inherited ones</b>. A type whose
    /// data lives on a data-only base class is still that data's carrier, and the conservation gate
    /// proves facts against these paths — walking only declared members left such an anchor with
    /// empty or partial <c>Paths</c>, so inherited facts read as absent and a refactor that dropped
    /// one would pass unnoticed.
    /// </summary>
    /// <remarks>
    /// Most-derived declaration wins, so an <c>override</c> or <c>new</c> property yields one path,
    /// not one per level. Indexers and statics are skipped: neither is a fact about an instance that
    /// a path can name. The walk stops at <see cref="SpecialType.System_Object"/> /
    /// <see cref="SpecialType.System_ValueType"/>, whose members are universal.
    /// </remarks>
    private static IEnumerable<IPropertySymbol> PublicReadableProperties(INamedTypeSymbol type)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (INamedTypeSymbol? current = type; current is not null; current = current.BaseType)
        {
            if (current.SpecialType is SpecialType.System_Object or SpecialType.System_ValueType) yield break;
            foreach (var prop in current.GetMembers().OfType<IPropertySymbol>())
            {
                if (prop.DeclaredAccessibility != Accessibility.Public) continue;
                if (prop.GetMethod is null) continue;
                if (prop.IsStatic) continue;
                if (prop.Parameters.Length > 0) continue;   // indexer — no stable path name
                if (!seen.Add(prop.Name)) continue;         // shadowed/overridden below
                yield return prop;
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
