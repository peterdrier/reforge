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
    /// <paramref name="solutionDirectory"/> is required to make declaration paths solution-relative
    /// before their directory segments are read — see <see cref="IsOnContractsSurface"/>.
    /// </summary>
    public static CanonicalReadDtoSet Derive(IEnumerable<ClassifiedType> classified, string solutionDirectory)
    {
        var bySection = new Dictionary<string, List<ClassifiedType>>(StringComparer.OrdinalIgnoreCase);
        var keys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var c in classified)
        {
            if (!IsCanonicalReadDto(c, solutionDirectory)) continue;
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
        if (byLength != 0) return byLength;
        var byName = string.CompareOrdinal(an, bn);
        // Simple names collide — a section may span two assemblies (X and X.Contracts) and holds
        // many namespaces. List.Sort is not stable, so a comparator that returned 0 here would let
        // enumeration order pick the primary anchor. Full identity is the last word.
        return byName != 0
            ? byName
            : string.CompareOrdinal(SolutionClassifier.TypeKey(a.Type), SolutionClassifier.TypeKey(b.Type));

        static int Rank(string name) => name.EndsWith(InfoSuffix, StringComparison.Ordinal) ? 0 : 1;
    }

    /// <summary>
    /// A canonical read DTO: an exported data type declared on the section's contracts surface.
    /// Interfaces, enums, delegates and static classes are excluded — a read API hands back data.
    /// </summary>
    private static bool IsCanonicalReadDto(ClassifiedType c, string solutionDirectory)
    {
        if (!c.IsExported) return false;
        if (c.Type.TypeKind is not (TypeKind.Class or TypeKind.Struct)) return false;
        if (c.Type.IsStatic) return false;
        if (!c.Tags.Contains("dto") && !IsDataCarrier(c.Type)) return false;
        return IsOnContractsSurface(
            c.Type.ContainingAssembly?.Name,
            c.Type.Locations.Where(l => l.IsInSource).Select(l => l.SourceTree?.FilePath),
            solutionDirectory);
    }

    /// <summary>
    /// Whether a type declared in <paramref name="assemblyName"/> at
    /// <paramref name="declarationPaths"/> sits on its section's contracts surface: the assembly is
    /// a <c>&lt;X&gt;.Contracts</c> assembly, or some declaration lives under a <c>Contracts/</c>
    /// folder. Both fold into the same section (see <see cref="AssemblySections"/>), so the section
    /// is whatever the type already resolved to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Every</b> declaration is inspected, not just the primary one. A partial type with one part
    /// under <c>Contracts/</c> and one part outside has several source locations, and which one
    /// <see cref="SolutionClassifier"/> picked as primary follows syntax-tree order — reading the
    /// primary alone would let a type enter and leave the set when files are merely reordered.
    /// Declaring any part of a type on the contracts surface publishes it.
    /// </para>
    /// <para>
    /// Each path is made <b>solution-relative first</b>. A raw <see cref="SyntaxTree.FilePath"/> is
    /// absolute, so a checkout under any ancestor directory named <c>Contracts</c> — say
    /// <c>/work/Contracts/MySolution</c> — would otherwise put every exported DTO-shaped type in the
    /// entire solution on a contracts surface.
    /// </para>
    /// </remarks>
    public static bool IsOnContractsSurface(
        string? assemblyName, IEnumerable<string?> declarationPaths, string solutionDirectory)
    {
        if (assemblyName is not null && AssemblySections.IsContractsAssembly(assemblyName)) return true;

        foreach (var path in declarationPaths)
        {
            if (string.IsNullOrEmpty(path)) continue;
            if (HasContractsSegment(LocationHelper.NormalizePath(path, solutionDirectory))) return true;
        }
        return false;
    }

    /// <summary>
    /// Whether the path has a <c>Contracts</c> directory segment. Both separators are split on:
    /// a solution-relative path is normalized to forward slashes, but a path outside the solution
    /// directory is returned as-is.
    /// </summary>
    private static bool HasContractsSegment(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        foreach (var segment in path.Split('/', '\\'))
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
