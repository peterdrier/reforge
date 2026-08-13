using Microsoft.CodeAnalysis;

namespace Reforge;

/// <summary>
/// Every section's canonical read DTOs — its published read API — derived from the solution
/// instead of configured. A canonical read DTO is a data type the section <b>exports</b> from its
/// contracts surface: declared in the section's <c>&lt;X&gt;.Contracts</c> assembly, or under a
/// <c>Contracts/</c> folder inside the section's own assembly. Both shapes occur in the wild and
/// both are structural.
/// </summary>
/// <remarks>
/// <para>
/// This replaces the hand-authored <c>canonicalReadDtos</c> config list. There is deliberately no
/// override: a section with neither a contracts assembly nor a contracts folder has not declared a
/// read API, and its score must say so rather than let JSON paper over a boundary that was never
/// drawn.
/// </para>
/// <para>
/// Location alone is never evidence. A <c>Contracts</c> folder or namespace holds internal types
/// too, and an internal type is not a published read API — every candidate is gated on
/// <see cref="SurfaceVisibility.IsExported"/>. Membership then follows the <b>declaring assembly</b>,
/// exactly like section membership, which is what resolves the old field's ambiguity: it was
/// consumed as one flat set of simple names by the scoring engine but section-scoped by the
/// section-shape analyzer.
/// </para>
/// </remarks>
public sealed class CanonicalReadDtoSet
{
    private const string ContractsFolder = "Contracts";
    private const string SettingsSuffix = "SettingsInfo";
    private const string InfoSuffix = "Info";

    /// <summary>Empty set — no section declares a read API.</summary>
    public static readonly CanonicalReadDtoSet Empty =
        new(new Dictionary<string, List<ClassifiedType>>(StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(StringComparer.Ordinal));

    private readonly Dictionary<string, List<ClassifiedType>> _bySection;
    private readonly HashSet<string> _keys;

    private CanonicalReadDtoSet(Dictionary<string, List<ClassifiedType>> bySection, HashSet<string> keys)
    {
        _bySection = bySection;
        _keys = keys;
    }

    /// <summary>
    /// Derives the canonical read DTOs of every section from the classified corpus.
    /// </summary>
    public static CanonicalReadDtoSet Derive(IEnumerable<ClassifiedType> classified)
    {
        var bySection = new Dictionary<string, List<ClassifiedType>>(StringComparer.OrdinalIgnoreCase);
        var keys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var c in classified)
        {
            if (!IsCanonicalReadDto(c)) continue;
            if (!bySection.TryGetValue(c.Group, out var list))
                bySection[c.Group] = list = new List<ClassifiedType>();
            list.Add(c);
            keys.Add(SolutionClassifier.TypeKey(c.Type));
        }

        foreach (var list in bySection.Values)
            list.Sort(ComparePreference);

        return new CanonicalReadDtoSet(bySection, keys);
    }

    /// <summary>
    /// The section's canonical read DTOs in anchor-preference order: <c>*Info</c> names first
    /// (that is what a primary read DTO is called by convention), then shortest name, then
    /// ordinal. Shortest-first is what makes <c>CampInfo</c> outrank <c>CampSeasonMemberInfo</c>
    /// as the section's primary anchor; the ordinal tiebreak makes the choice deterministic.
    /// </summary>
    public IReadOnlyList<ClassifiedType> ForSection(string section) =>
        _bySection.TryGetValue(section, out var list) ? list : Array.Empty<ClassifiedType>();

    /// <summary>Sections that export at least one canonical read DTO.</summary>
    public IEnumerable<string> Sections => _bySection.Keys;

    /// <summary>
    /// Whether the type is some section's canonical read DTO. Matched on symbol identity
    /// (declaring assembly + fully qualified name), not on the simple name: two assemblies may
    /// each declare a <c>UserInfo</c>, and only one of them may be exported from a contracts
    /// surface.
    /// </summary>
    public bool Contains(ITypeSymbol type) =>
        _keys.Contains(SolutionClassifier.TypeKey(type.OriginalDefinition));

    private static int ComparePreference(ClassifiedType a, ClassifiedType b)
    {
        var an = a.Type.Name;
        var bn = b.Type.Name;
        var byInfo = Rank(an).CompareTo(Rank(bn));
        if (byInfo != 0) return byInfo;
        var byLength = an.Length.CompareTo(bn.Length);
        return byLength != 0 ? byLength : string.CompareOrdinal(an, bn);

        static int Rank(string name) => name.EndsWith(InfoSuffix, StringComparison.Ordinal) ? 0 : 1;
    }

    /// <summary>
    /// A canonical read DTO: an exported data type declared on the section's contracts surface.
    /// Interfaces, enums, delegates and static classes are excluded — a read API hands back data.
    /// </summary>
    private static bool IsCanonicalReadDto(ClassifiedType c)
    {
        if (!c.IsExported) return false;
        if (c.Type.TypeKind is not (TypeKind.Class or TypeKind.Struct)) return false;
        if (c.Type.IsStatic) return false;
        if (!c.Tags.Contains("dto") && !IsDataCarrier(c.Type)) return false;
        return IsContractsDeclaration(c);
    }

    /// <summary>
    /// Whether the type is declared on its section's contracts surface: in the section's
    /// <c>&lt;X&gt;.Contracts</c> assembly, or under a <c>Contracts/</c> folder in the section's own
    /// assembly. Both fold into the same section (see <see cref="AssemblySections"/>), so the
    /// section is whatever the type already resolved to.
    /// </summary>
    private static bool IsContractsDeclaration(ClassifiedType c)
    {
        var assembly = c.Type.ContainingAssembly?.Name;
        if (assembly is not null && AssemblySections.IsContractsAssembly(assembly)) return true;

        foreach (var segment in c.File.Split('/'))
            if (segment.Equals(ContractsFolder, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>
    /// Behavioral DTO test: a data carrier is a non-static class or struct with at least one
    /// public property and no public methods. Used as the fallback when the active config carries
    /// no <c>dto</c> classification rule, and to admit DTOs whose names don't match the
    /// conventional suffixes (<c>*Hit</c>, <c>*Totals</c>, <c>*Row</c>).
    /// </summary>
    public static bool IsDataCarrier(INamedTypeSymbol t)
    {
        if (t.TypeKind is not (TypeKind.Class or TypeKind.Struct)) return false;
        if (t.IsStatic) return false;
        if (!t.Locations.Any(l => l.IsInSource)) return false;
        int props = 0, methods = 0;
        foreach (var m in t.GetMembers())
        {
            if (m.IsImplicitlyDeclared || m.DeclaredAccessibility != Accessibility.Public) continue;
            switch (m)
            {
                case IPropertySymbol: props++; break;
                case IMethodSymbol meth when meth.MethodKind == MethodKind.Ordinary && meth.AssociatedSymbol is null: methods++; break;
            }
        }
        return props >= 1 && methods == 0;
    }

    /// <summary>Whether the name is the settings DTO by convention (<c>*SettingsInfo</c>).</summary>
    public static bool IsSettingsDtoName(string name) =>
        name.EndsWith(SettingsSuffix, StringComparison.Ordinal);
}
