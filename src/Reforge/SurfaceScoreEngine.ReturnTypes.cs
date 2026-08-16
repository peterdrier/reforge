using Microsoft.CodeAnalysis;

namespace Reforge;

// Pass 4 — the canonical read-DTO credit: what a published method hands back.
public sealed partial class SurfaceScoreEngine
{
    /// <summary>
    /// canonicalReadDtoReturn — credit when a published method returns a section's canonical read
    /// DTO (the project-blessed read API). Negative weight, so it pulls the score down.
    /// </summary>
    /// <remarks>
    /// This pass used to carry a second rule, <c>methodReturnsEntityAcrossSection</c>, penalizing a
    /// public method for returning another section's domain entity. Retired: it was specific to one
    /// codebase's layout, and the constraint it approximated is better enforced by keeping entities
    /// non-public than by pricing the leak after the fact. See the changelog.
    /// </remarks>
    private void ScoreReturnTypeRules(
        List<ClassifiedType> classified,
        ScoreReport report)
    {
        var canonicalWeight = _config.Weight("canonicalReadDtoReturn");
        if (canonicalWeight == 0) return;

        // The canonical read DTOs of every section, derived from what each section exports from
        // its contracts surface. The credit applies solution-wide — a Tickets method returning
        // Users's canonical DTO still earns it — but membership is per-symbol, not per-name: two
        // assemblies may each declare a UserInfo and only one may be a published read API.
        var canonical = CanonicalReadDtoSet.Derive(classified, _solutionDirectory);

        foreach (var c in classified)
        {
            if (c.Type.TypeKind != TypeKind.Class) continue;
            // The credit is for what a published method hands back. An internal method hands it
            // back only within the assembly, so nothing is published and nothing is credited.
            if (!c.IsExported) continue;
            // Controllers return HTTP responses; returning a read DTO from one is not adoption
            // of the section's read API in the sense the credit rewards.
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

                if (canonical.Contains(named))
                    AddEntry(report, c.Group, "canonicalReadDtoReturn", canonicalWeight, m, file, line,
                        $"{m.Name} -> {named.Name}");
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
