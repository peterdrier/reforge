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
        var all = classified as ICollection<ClassifiedType> ?? classified.ToList();
        var analyzedAssemblies = AnalyzedAssemblies(all);

        foreach (var c in all)
        {
            if (!IsCanonicalReadDto(c, solutionDirectory, analyzedAssemblies)) continue;
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
    private static bool IsCanonicalReadDto(ClassifiedType c, string solutionDirectory,
        HashSet<string> analyzedAssemblies)
    {
        if (!c.IsExported) return false;
        if (c.Type.TypeKind is not (TypeKind.Class or TypeKind.Struct)) return false;
        if (c.Type.IsStatic) return false;
        if (!c.Tags.Contains("dto") && !IsDataCarrier(c.Type, analyzedAssemblies)) return false;
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
    /// Behavioral DTO test: a data carrier is a non-static class or struct with at least one public
    /// readable <i>instance</i> property and no consumer-callable behavior. Used as the fallback
    /// when the active config carries no <c>dto</c> classification rule, and to admit DTOs whose
    /// names don't match the conventional suffixes (<c>*Hit</c>, <c>*Totals</c>, <c>*Row</c>).
    /// "Behavior" is every shape a consumer can invoke — ordinary methods, operators, conversions,
    /// events, explicit interface implementations, and anything inherited from a base or supplied
    /// by a default interface method. The property side matches
    /// <see cref="DtoInventory"/> exactly, so an admitted type always has facts to inventory.
    /// </summary>
    public static bool IsDataCarrier(INamedTypeSymbol t, HashSet<string> analyzedAssemblies,
        bool countIndexersAsData = false)
    {
        if (t.TypeKind is not (TypeKind.Class or TypeKind.Struct)) return false;
        if (t.IsStatic) return false;
        if (!IsInAnalyzedSolution(t, analyzedAssemblies)) return false;

        // Allowlist, not blacklist. Asking "which member shapes are behavior?" is an open-ended
        // question — ordinary methods, then explicit implementations, then operators, conversions,
        // events, default interface methods, each found one at a time. Asking "is every member
        // carried data or invisible to a consumer?" is closed: anything unrecognised disqualifies
        // by default, so a shape nobody thought of fails safe instead of publishing a behavioral
        // type as a read DTO.
        //
        // The walk climbs base types — `class SearchHit : List<int>` declares only a property but
        // hands a consumer Add/Remove/Insert. System.Object and System.ValueType stop it: their
        // members are universal rather than a published API choice.
        //
        // Past the solution boundary the two halves of the question separate, and conflating them
        // was a defect in both directions. BEHAVIOUR counts wherever it is declared: a consumer can
        // call the base's methods on this type, so `SearchHit : List<int>` is not a data carrier no
        // matter whose assembly List lives in. DATA does not: a framework base's properties are not
        // this section's published shape, and counting them admitted `ExpenseLineProofRows :
        // Migration` — an EF migration whose own Up/Down are protected — as a read DTO on the
        // strength of EF's TargetModel/UpOperations/DownOperations. Stopping the walk at the
        // boundary fixes the migration and loses SearchHit; splitting the two keeps both.
        int props = 0;
        for (INamedTypeSymbol? current = t; current is not null; current = current.BaseType)
        {
            if (current.SpecialType is SpecialType.System_Object or SpecialType.System_ValueType) break;
            bool ours = IsInAnalyzedSolution(current, analyzedAssemblies);
            foreach (var m in current.GetMembers())
            {
                if (IsCarriedData(m) || (countIndexersAsData && IsPublishedIndexer(m)))
                {
                    if (ours) props++;
                    continue;
                }
                if (IsInvisibleToConsumers(m)) continue;
                return false;
            }
        }

        // A default interface method is behavior the type never declares at all. Only NON-abstract
        // members qualify — an abstract one is either implemented on the type (already judged
        // above) or unimplementable. That distinction is load-bearing: every record implements
        // IEquatable<T>, so treating abstract interface members as behavior would disqualify every
        // record in the solution.
        foreach (var iface in t.AllInterfaces)
            foreach (var m in iface.GetMembers())
                if (m is { IsAbstract: false, IsStatic: false, DeclaredAccessibility: Accessibility.Public }
                    and (IMethodSymbol or IEventSymbol))
                    return false;

        return props >= 1;
    }

    /// <summary>
    /// A member that IS the carried data: a public, readable, non-static, non-indexer property.
    /// Exactly what <see cref="DtoInventory"/> turns into an anchor path, so a type admitted on
    /// these always has facts to inventory rather than anchoring an empty path set.
    /// </summary>
    internal static bool IsCarriedData(ISymbol m) =>
        m is IPropertySymbol { IsStatic: false, Parameters.Length: 0, DeclaredAccessibility: Accessibility.Public, GetMethod: not null };

    /// <summary>
    /// A member no external consumer can reach, so it says nothing about whether the type is a data
    /// carrier: compiler-synthesized members (a record's <c>Equals</c>/<c>ToString</c>/
    /// <c>Deconstruct</c>), constructors, property and event accessors, nested type declarations,
    /// and anything non-public. An <b>explicit interface implementation</b> is pointedly NOT here —
    /// it is <c>private</c> on the symbol but callable by anyone who casts.
    /// </summary>
    internal static bool IsInvisibleToConsumers(ISymbol m)
    {
        if (m.IsImplicitlyDeclared) return true;
        if (m is IMethodSymbol { MethodKind: MethodKind.ExplicitInterfaceImplementation }) return false;
        // Same reasoning for an event: Roslyn reports an explicit implementation as `private`, so
        // the accessibility line below read it as unreachable, while anyone holding the interface
        // can subscribe. The AllInterfaces scan does not catch it either — the interface's own
        // declaration is abstract, and that scan counts only non-abstract members — so the shape
        // was missed from both directions.
        if (m is IEventSymbol { ExplicitInterfaceImplementations.IsEmpty: false }) return false;
        if (m.DeclaredAccessibility != Accessibility.Public) return true;
        return m switch
        {
            IMethodSymbol meth => meth.MethodKind
                is MethodKind.Constructor or MethodKind.StaticConstructor or MethodKind.Destructor
                or MethodKind.PropertyGet or MethodKind.PropertySet
                or MethodKind.EventAdd or MethodKind.EventRemove or MethodKind.EventRaise,
            // A nested type is a declaration, not a member a consumer invokes on an instance.
            INamedTypeSymbol => true,
            // A static or indexer property is not behavior, but not a nameable instance fact
            // either — it simply is not evidence in either direction.
            IPropertySymbol => true,
            // A const or field carries no behavior. Fields are deliberately not counted as data
            // either: DtoInventory builds paths from properties, so counting them would admit types
            // whose inventory comes back empty.
            IFieldSymbol => true,
            _ => false
        };
    }

    /// <summary>
    /// A public instance indexer. <see cref="IsCarriedData"/> excludes these on purpose — an indexer
    /// is not a nameable fact, so it cannot become an inventory path — but the DTO scorer charges
    /// them as published properties, and a type whose only properties are indexers would otherwise
    /// score as nothing at all: not a data carrier, so no <c>publicDtoType</c>, and never reached,
    /// so no per-indexer charge either. Counted only for the caller that charges them, via
    /// <c>countIndexersAsData</c>.
    /// </summary>
    internal static bool IsPublishedIndexer(ISymbol m) =>
        m is IPropertySymbol
        {
            IsStatic: false, Parameters.Length: > 0,
            DeclaredAccessibility: Accessibility.Public, GetMethod: not null
        };

    /// <summary>
    /// The assemblies the run analysed, read off the classified corpus. One derivation, so every
    /// caller of <see cref="IsDataCarrier"/> draws the boundary in the same place.
    /// </summary>
    public static HashSet<string> AnalyzedAssemblies(IEnumerable<ClassifiedType> classified) =>
        new(classified.Select(c => c.Type.ContainingAssembly?.Name).OfType<string>(), StringComparer.Ordinal);

    /// <summary>
    /// The solution boundary, by <b>assembly membership</b> rather than <c>Location.IsInSource</c>.
    /// The two agree only for the common project layout: a project referenced as a compiled DLL
    /// arrives as a metadata symbol with no source location while its assembly is very much part of
    /// the analysed set. Same definition <see cref="SolutionClassifier"/> uses.
    /// </summary>
    private static bool IsInAnalyzedSolution(ISymbol t, HashSet<string> analyzedAssemblies) =>
        t.ContainingAssembly?.Name is { } name && analyzedAssemblies.Contains(name);

    /// <summary>Whether the name is the settings DTO by convention (<c>*SettingsInfo</c>).</summary>
    public static bool IsSettingsDtoName(string name) =>
        name.EndsWith(SettingsSuffix, StringComparison.Ordinal);
}
