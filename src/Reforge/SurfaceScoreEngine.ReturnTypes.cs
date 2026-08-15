using Microsoft.CodeAnalysis;

namespace Reforge;

// Pass 4 — return-type rules: canonical read-DTO credit and the cross-section entity leak.
// Both inspect what a published method hands back, so they share one walk.
public sealed partial class SurfaceScoreEngine
{
    /// <summary>
    /// Two rules share a single walk over public methods because both inspect the return type:
    /// <list type="bullet">
    ///   <item>canonicalReadDtoReturn — credit when the method returns a section's canonical
    ///         read DTO (the project-blessed read API). Negative weight.</item>
    ///   <item>methodReturnsEntityAcrossSection — penalty when the return type is classified
    ///         as an entity (domain model) AND lives in a different section than the method's
    ///         containing type. This is the "service boundary exists but leaks EF/domain entity
    ///         anyway" smell.</item>
    /// </list>
    /// Canonical DTOs are explicitly exempt from the entity penalty even if their simple name
    /// would match the entity classification — canonical DTOs are by definition the read API.
    /// </summary>
    private void ScoreReturnTypeRules(
        List<ClassifiedType> classified,
        Dictionary<string, ClassifiedType> typesByDisplay,
        ScoreReport report)
    {
        var canonicalWeight = _config.Weight("canonicalReadDtoReturn");
        var entityWeight = _config.Weight("methodReturnsEntityAcrossSection");
        if (canonicalWeight == 0 && entityWeight == 0) return;

        // The canonical read DTOs of every section, derived from what each section exports from
        // its contracts surface. The credit applies solution-wide — a Tickets method returning
        // Users's canonical DTO still earns it — but membership is per-symbol, not per-name: two
        // assemblies may each declare a UserInfo and only one may be a published read API.
        var canonical = CanonicalReadDtoSet.Derive(classified, _solutionDirectory);

        foreach (var c in classified)
        {
            if (c.Type.TypeKind != TypeKind.Class) continue;
            // Both rules judge what a published method hands back. An internal method hands it
            // back only within the assembly, so neither the credit nor the leak penalty applies.
            if (!c.IsExported) continue;
            // Controllers' return types are HTTP responses, not domain leaks.
            if (c.Tags.Contains("controller")) continue;

            foreach (var member in c.Type.GetMembers())
            {
                if (member is not IMethodSymbol m) continue;
                if (m.MethodKind != MethodKind.Ordinary) continue;
                if (m.AssociatedSymbol is not null) continue;
                if (m.IsImplicitlyDeclared) continue;
                if (m.DeclaredAccessibility != Accessibility.Public) continue;

                var ret = UnwrapTaskLike(m.ReturnType);
                if (ret.SpecialType != SpecialType.None) continue; // primitives, void
                ret = UnwrapCollection(ret);
                if (ret is not INamedTypeSymbol named) continue;
                if (!named.Locations.Any(l => l.IsInSource)) continue;

                var loc = m.Locations.FirstOrDefault(l => l.IsInSource);
                var (file, line) = LocateMember(loc, c);

                // Canonical DTO credit takes precedence — exempt from the entity penalty. The
                // exemption holds even when the credit is weighted to 0: a canonical DTO is the
                // section's read API by definition, so returning one is never an entity leak.
                if (canonical.Contains(named))
                {
                    if (canonicalWeight != 0)
                        AddEntry(report, c.Group, "canonicalReadDtoReturn", canonicalWeight, m, file, line,
                            $"{m.Name} -> {named.Name}");
                    continue;
                }

                if (entityWeight == 0) continue;
                if (!typesByDisplay.TryGetValue(SolutionClassifier.TypeKey(named), out var returnTypeInfo)) continue;
                if (!returnTypeInfo.Tags.Contains("entity")) continue;
                if (string.Equals(returnTypeInfo.Group, c.Group, StringComparison.OrdinalIgnoreCase)) continue;

                AddEntry(report, c.Group, "methodReturnsEntityAcrossSection", entityWeight, m, file, line,
                    $"{m.Name} -> {named.Name} (entity in '{returnTypeInfo.Group}')");
            }
        }
    }

    /// <summary>
    /// Unwraps generic single-element containers (IEnumerable&lt;T&gt;, IReadOnlyList&lt;T&gt;,
    /// List&lt;T&gt;, etc.) so a method returning Task&lt;IReadOnlyList&lt;User&gt;&gt; still
    /// trips the entity-leak rule on <c>User</c>.
    /// </summary>
    private static ITypeSymbol UnwrapCollection(ITypeSymbol t)
    {
        if (t is INamedTypeSymbol n && n.IsGenericType && n.TypeArguments.Length == 1)
        {
            var od = n.OriginalDefinition.ToDisplayString();
            if (od.StartsWith("System.Collections.Generic.", StringComparison.Ordinal)
                || od.StartsWith("System.Collections.Immutable.", StringComparison.Ordinal)
                || od == "System.Collections.IEnumerable")
            {
                return n.TypeArguments[0];
            }
        }
        if (t is IArrayTypeSymbol arr)
            return arr.ElementType;
        return t;
    }
}
