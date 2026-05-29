using Microsoft.CodeAnalysis;

namespace Reforge;

public enum ReadMethodKind
{
    PrimitiveRead,    // returns primary Info DTO (or collection of it) - healthy
    SettingsRead,     // returns settings DTO - healthy
    Search,           // real search shape (query/paging input + search-hit/result output) - healthy
    ProjectionSummary,// returns a non-primary DTO / collection - charged
    Predicate,        // returns bool - charged
    ScalarFact,       // returns a single primitive/string/Guid/DateTime - charged
    UiBuilder         // returns a composed view DTO (*Data/*ViewModel/*PageModel) - charged
}

/// <summary>
/// Behavioral classifier for read-service-interface methods. Decides by return/parameter shape,
/// not by method name alone - renaming a projection to Search* must not make it "healthy".
/// </summary>
public static class ReadSurface
{
    public static bool IsCharged(ReadMethodKind k) =>
        k is ReadMethodKind.ProjectionSummary or ReadMethodKind.Predicate
          or ReadMethodKind.ScalarFact or ReadMethodKind.UiBuilder;

    public static ReadMethodKind Classify(IMethodSymbol m, string? primaryInfoDto, string? settingsInfoDto)
    {
        var ret = UnwrapTaskLike(m.ReturnType);

        if (ret.SpecialType == SpecialType.System_Boolean) return ReadMethodKind.Predicate;

        var element = UnwrapCollection(ret);

        if (ReferenceEquals(element, ret) && IsScalarFact(ret)) return ReadMethodKind.ScalarFact;

        var elementName = (element as INamedTypeSymbol)?.Name;

        if (elementName is not null && primaryInfoDto is not null && elementName == primaryInfoDto)
            return ReadMethodKind.PrimitiveRead;
        if (elementName is not null && settingsInfoDto is not null && elementName == settingsInfoDto)
            return ReadMethodKind.SettingsRead;

        if (HasSearchInput(m) && IsSearchResult(element)) return ReadMethodKind.Search;

        if (elementName is not null &&
            (elementName.EndsWith("Data", StringComparison.Ordinal)
             || elementName.EndsWith("ViewModel", StringComparison.Ordinal)
             || elementName.EndsWith("PageModel", StringComparison.Ordinal)))
            return ReadMethodKind.UiBuilder;

        return ReadMethodKind.ProjectionSummary;
    }

    private static bool IsScalarFact(ITypeSymbol t)
    {
        // bool is caught as Predicate before this is called, so it's intentionally excluded here.
        if (t.SpecialType is SpecialType.System_String or SpecialType.System_Int32
            or SpecialType.System_Int64 or SpecialType.System_Double
            or SpecialType.System_Decimal) return true;
        var n = t.Name;
        return n is "Guid" or "DateTime" or "DateTimeOffset" or "DateOnly" or "TimeOnly";
    }

    private static bool HasSearchInput(IMethodSymbol m)
    {
        foreach (var p in m.Parameters)
        {
            if (p.Type.Name == "CancellationToken") continue;
            var tn = p.Type.Name;
            if (tn.EndsWith("Query", StringComparison.Ordinal)
                || tn.EndsWith("Filter", StringComparison.Ordinal)
                || tn.EndsWith("SearchRequest", StringComparison.Ordinal)
                || tn.EndsWith("Criteria", StringComparison.Ordinal)
                || p.Name is "page" or "pageSize" or "skip" or "take")
                return true;
        }
        return false;
    }

    private static bool IsSearchResult(ITypeSymbol element)
    {
        var n = (element as INamedTypeSymbol)?.Name ?? element.Name;
        return n.EndsWith("SearchHit", StringComparison.Ordinal)
            || n.EndsWith("Hit", StringComparison.Ordinal)
            || n.EndsWith("SearchResult", StringComparison.Ordinal)
            || n.EndsWith("SearchPage", StringComparison.Ordinal);
    }

    private static ITypeSymbol UnwrapTaskLike(ITypeSymbol t)
    {
        if (t is INamedTypeSymbol n && n.IsGenericType)
        {
            var name = n.OriginalDefinition.ToDisplayString();
            if (name is "System.Threading.Tasks.Task<TResult>" or "System.Threading.Tasks.ValueTask<TResult>")
                return n.TypeArguments[0];
        }
        return t;
    }

    private static ITypeSymbol UnwrapCollection(ITypeSymbol t)
    {
        if (t is INamedTypeSymbol n && n.IsGenericType && n.TypeArguments.Length == 1)
        {
            var od = n.OriginalDefinition.ToDisplayString();
            if (od.StartsWith("System.Collections.Generic.", StringComparison.Ordinal)
                || od.StartsWith("System.Collections.Immutable.", StringComparison.Ordinal)
                || od == "System.Collections.IEnumerable")
                return n.TypeArguments[0];
        }
        if (t is IArrayTypeSymbol arr) return arr.ElementType;
        return t;
    }
}
