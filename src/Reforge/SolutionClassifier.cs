using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Reforge;

/// <summary>
/// The shared type-classification pass: opens each non-test project, enumerates source-declared
/// types, resolves each into a section (group) and assigns role tags. Extracted from
/// SurfaceScoreEngine so surface-score, section-shape, and the baseline gate share one pass.
/// </summary>
/// <remarks>
/// Section identity is the type's <b>containing assembly</b> (see <see cref="AssemblySections"/>),
/// never config. An assembly boundary is structural and compiler-enforced — strictly stronger than
/// either a name glob or a path glob, and it cannot drift from the solution because it *is* the
/// solution. Config carries policy only; it no longer describes where sections are.
/// </remarks>
public static class SolutionClassifier
{
    public static async Task<IReadOnlyList<ClassifiedType>> ClassifyAsync(
        Solution solution, SurfaceScoreConfig config, string solutionDirectory, CancellationToken ct)
    {
        var seenByDisplay = new HashSet<string>(StringComparer.Ordinal);
        var projects = solution.Projects
            .Where(p => !p.Name.Contains("Test", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var analyzed = new HashSet<string>(projects.Select(p => p.AssemblyName), StringComparer.Ordinal);

        // Pass 1 — collect types with their raw assembly name. The section name can't be assigned
        // yet: it depends on the prefix shared across the assemblies that actually declare types.
        var collected = new List<(INamedTypeSymbol Type, string Assembly, HashSet<string> Tags, string File, Location Location)>();

        foreach (var project in projects)
        {
            var compilation = await project.GetCompilationAsync(ct);
            if (compilation is null) continue;

            foreach (var type in EnumerateTypes(compilation.GlobalNamespace))
            {
                if (!type.Locations.Any(l => l.IsInSource)) continue;
                if (type.IsImplicitlyDeclared) continue;
                // Internal types stay in the corpus on purpose: their implementation is still
                // complexity the section carries, and the sizing rules must see it. What they no
                // longer do is score as surface — see ClassifiedType.IsExported.
                if (type.DeclaredAccessibility == Accessibility.Private) continue;

                // A compilation's global namespace also reaches referenced projects' source types,
                // so the enumerating project is NOT the owner — the containing assembly is. Types
                // from assemblies outside the analyzed set (test projects reached by reference)
                // drop out here.
                var assembly = type.ContainingAssembly?.Name;
                if (assembly is null || !analyzed.Contains(assembly)) continue;
                // Dedup is per ASSEMBLY, not per display name. Every project's compilation reaches
                // its references' source types, so the same type is enumerated repeatedly and must
                // collapse — but two assemblies may legitimately declare the same fully qualified
                // name (an internal helper, a generated type). Keying on the name alone dropped the
                // second one, and an assembly whose every type collided vanished from the section
                // map entirely.
                if (!seenByDisplay.Add($"{assembly}|{type.ToDisplayString()}")) continue;

                var primaryLocation = type.Locations.First(l => l.IsInSource);
                var filePath = primaryLocation.SourceTree?.FilePath ?? "";
                var relPath = LocationHelper.NormalizePath(filePath, solutionDirectory);
                var nsName = type.ContainingNamespace?.ToDisplayString() ?? "";

                collected.Add((type, assembly, Classify(config, type, relPath, nsName), relPath, primaryLocation));
            }
        }

        // Pass 1.5 — demote name-classified write surfaces that nothing actually writes through.
        DemoteReadOnlyServiceInterfaces(collected, ct);

        // Pass 2 — name the sections. Only type-declaring assemblies participate: a docs/tooling
        // project that ships no C# is not a section, and letting it into the prefix calculation
        // would strip nothing at all from the real ones.
        var sectionByAssembly = AssemblySections.Resolve(collected.Select(c => c.Assembly));

        return collected
            .Select(c => new ClassifiedType(c.Type, sectionByAssembly[c.Assembly], c.Tags, c.File, c.Location))
            .ToList();
    }

    /// <summary>
    /// Reclassifies an interface the name patterns called a full (write-capable) service interface as
    /// a <c>readServiceInterface</c> when no implementation of any of its members observably mutates
    /// persistent state.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>fullServiceInterface</c> is assigned by the name pattern <c>I*Service</c>, and the read
    /// escape hatch only catches <c>I*ServiceRead</c> / <c>I*ReadService</c> / <c>I*QueryService</c>.
    /// So an all-<c>Get*</c> facade named <c>IAuditViewerService</c> was priced as published <b>write</b>
    /// surface. On the Humans corpus that was 10 of 47 exported write interfaces — 20 methods — which
    /// is issue #54.
    /// </para>
    /// <para>
    /// For methods the test is behavioral, not nominal: it reuses
    /// <see cref="ImplementationComplexity.IsMutation"/>, which decides from the implementation body
    /// (a persistence-commit call) or the method's shape (returns no data, non-query verb). That is
    /// what makes this rename-proof, and it is why a method's own declaration cannot answer it — an
    /// interface method has no body, so the question is only decidable at the implementation.
    /// </para>
    /// <para>
    /// Two member shapes ARE decidable on the declaration and are checked there: a <b>settable
    /// property</b> and an <b>event</b>. Either hands every consumer a mutation directly, and no
    /// implementation body can withdraw it, so one of those is sufficient on its own — no
    /// implementation need be found at all.
    /// </para>
    /// <para>
    /// Membership is read from the interface <b>and its base interfaces</b>. <c>GetMembers()</c>
    /// returns only what a type declares itself, so <c>IOrderService : ICrudService&lt;Order&gt;</c>
    /// would look empty and demote while its consumers get a full set of writes.
    /// </para>
    /// <para>
    /// Demotion requires <b>evidence of read-only-ness</b>, not merely absence of evidence of writing.
    /// Two gaps therefore preserve the name-derived classification rather than repricing on nothing —
    /// silently repricing surface on an analysis gap is the failure this codebase has been bitten by
    /// before (see <see cref="SurfaceVisibility"/> and issue #51):
    /// </para>
    /// <list type="bullet">
    ///   <item><b>No implementation in the solution.</b> The walk skips test projects and cannot see
    ///         other assemblies, so "not found" means unknown, not read-only.</item>
    ///   <item><b>No implementing type accounts for the whole surface.</b> An abstract class may list
    ///         the interface and leave its members abstract; a bodyless data-returning declaration
    ///         reads as a query under the shape heuristic, which would demote on an absence. Evidence
    ///         of <i>writing</i> still counts from a partial observation — it is only the read-only
    ///         conclusion that needs every member accounted for by some one implementer.</item>
    /// </list>
    /// <para>
    /// Demoted interfaces are not exempted — they still score, at <c>readServiceInterfaceMethod</c>
    /// rather than <c>fullServiceInterfaceMethod</c>. A published read facade is real surface; it is
    /// just not a write commitment.
    /// </para>
    /// </remarks>
    private static void DemoteReadOnlyServiceInterfaces(
        List<(INamedTypeSymbol Type, string Assembly, HashSet<string> Tags, string File, Location Location)> collected,
        CancellationToken ct)
    {
        var candidates = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var c in collected)
            if (c.Type.TypeKind == TypeKind.Interface && c.Tags.Contains("fullServiceInterface"))
                candidates[TypeKey(c.Type)] = c.Tags;
        if (candidates.Count == 0) return;

        var observed = new HashSet<string>(StringComparer.Ordinal);
        var writes = new HashSet<string>(StringComparer.Ordinal);

        // Pass A -- write capability that needs no implementation to see. A settable property or an
        // event ON the interface is already the write commitment: the declaration hands every
        // consumer a mutation, and no body could withdraw it.
        foreach (var c in collected)
        {
            if (c.Type.TypeKind != TypeKind.Interface) continue;
            var key = TypeKey(c.Type);
            if (!candidates.ContainsKey(key)) continue;
            foreach (var member in PublishedMembers(c.Type))
            {
                ct.ThrowIfCancellationRequested();
                if (member is IPropertySymbol { SetMethod: not null } or IEventSymbol)
                {
                    writes.Add(key);
                    break;
                }
            }
        }

        // Pass B -- everything else is decided at the implementation.
        foreach (var c in collected)
        {
            if (c.Type.TypeKind == TypeKind.Interface) continue;
            foreach (var iface in c.Type.AllInterfaces)
            {
                ct.ThrowIfCancellationRequested();
                var key = TypeKey(iface.OriginalDefinition);
                if (!candidates.ContainsKey(key)) continue;

                bool complete = true;
                foreach (var member in PublishedMembers(iface))
                {
                    if (member is not IMethodSymbol { MethodKind: MethodKind.Ordinary } m) continue;

                    // An abstract or bodyless implementation is not a behavioral observation. An
                    // abstract class may list the interface and leave every member abstract, and the
                    // shape heuristic would then read a data-returning declaration as a read and
                    // demote on nothing. The concrete override may be in a skipped test project or
                    // another assembly entirely.
                    var impl = c.Type.FindImplementationForInterfaceMember(m) as IMethodSymbol;
                    var syntax = impl is { IsAbstract: false } && impl.DeclaringSyntaxReferences.Length > 0
                        ? impl.DeclaringSyntaxReferences[0].GetSyntax(ct) as BaseMethodDeclarationSyntax
                        : null;
                    if (syntax is null || (syntax.Body is null && syntax.ExpressionBody is null))
                    {
                        complete = false;
                        continue;
                    }

                    // Evidence of writing counts from a partial observation too -- it is only the
                    // read-only conclusion that needs the whole surface accounted for.
                    if (ImplementationComplexity.IsMutation(impl!, syntax)) writes.Add(key);
                }

                if (complete) observed.Add(key);
            }
        }

        foreach (var (key, tags) in candidates)
        {
            if (!observed.Contains(key) || writes.Contains(key)) continue;
            tags.Remove("fullServiceInterface");
            tags.Add("readServiceInterface");
        }
    }

    /// <summary>
    /// Every member an interface publishes to a consumer, its own and its base interfaces'.
    /// </summary>
    /// <remarks>
    /// <c>GetMembers()</c> returns only what a type declares itself, so
    /// <c>IOrderService : ICrudService&lt;Order&gt;</c> appears to declare nothing at all. Reading
    /// only the declared members would demote it while <c>ICrudService&lt;Order&gt;</c> hands its
    /// consumers a full set of writes. The base interfaces come from <see cref="INamedTypeSymbol.AllInterfaces"/>
    /// on the CONSTRUCTED interface, so type arguments are already substituted and each member can be
    /// handed to <c>FindImplementationForInterfaceMember</c> as-is.
    /// </remarks>
    private static IEnumerable<ISymbol> PublishedMembers(INamedTypeSymbol iface)
    {
        foreach (var member in iface.GetMembers()) yield return member;
        foreach (var baseInterface in iface.AllInterfaces)
            foreach (var member in baseInterface.GetMembers())
                yield return member;
    }

    /// <summary>
    /// Identity of a type for every lookup map in the scoring passes: <c>declaringAssembly|fullyQualifiedName</c>.
    /// The fully qualified name alone is NOT unique across a solution — two assemblies may each
    /// declare an internal <c>Shared.IOrderService</c> — and collapsing them would resolve a consumer
    /// to the wrong section, producing false cross-section findings or suppressing real ones. Keying
    /// on the <b>declaring</b> assembly is what makes a cross-assembly lookup still land correctly:
    /// a consumer in A injecting a type declared in B resolves through B's key, because the symbol
    /// it holds is B's.
    /// </summary>
    public static string TypeKey(ISymbol type) =>
        $"{type.ContainingAssembly?.Name}|{type.ToDisplayString()}";

    private static IEnumerable<INamedTypeSymbol> EnumerateTypes(INamespaceSymbol ns)
    {
        foreach (var m in ns.GetMembers())
        {
            switch (m)
            {
                case INamespaceSymbol child:
                    foreach (var t in EnumerateTypes(child)) yield return t;
                    break;
                case INamedTypeSymbol type:
                    yield return type;
                    foreach (var nested in type.GetTypeMembers())
                        yield return nested;
                    break;
            }
        }
    }

    private static HashSet<string> Classify(SurfaceScoreConfig config, INamedTypeSymbol type, string filePath, string namespaceName)
    {
        var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, rule) in config.Classifications)
            if (Matches(rule, type, filePath, namespaceName))
                tags.Add(name);

        if (type.TypeKind == TypeKind.Interface)
        {
            tags.Remove("repositoryImplementation");
            tags.Remove("applicationService");
            tags.Remove("controller");
            tags.Remove("backgroundJob");
            if (tags.Contains("readServiceInterface")) tags.Remove("fullServiceInterface");
            if (tags.Contains("repositoryInterface")) tags.Remove("fullServiceInterface");
        }
        else
        {
            tags.Remove("readServiceInterface");
            tags.Remove("fullServiceInterface");
            tags.Remove("repositoryInterface");
            if (tags.Contains("repositoryImplementation")) tags.Remove("applicationService");
        }
        return tags;
    }

    private static bool Matches(ClassificationRule rule, INamedTypeSymbol type, string filePath, string namespaceName)
    {
        foreach (var p in rule.NamePatterns)
            if (GlobMatcher.MatchesName(type.Name, p)) return true;
        foreach (var p in rule.Paths)
            if (GlobMatcher.MatchesPath(filePath, p)) return true;
        foreach (var n in rule.Namespaces)
            if (namespaceName.StartsWith(n, StringComparison.Ordinal)) return true;
        foreach (var i in rule.Inherits)
            if (InheritsByName(type, i)) return true;
        foreach (var a in rule.AttributeNames)
            if (type.GetAttributes().Any(at => at.AttributeClass?.Name == a || at.AttributeClass?.Name == a + "Attribute")) return true;
        return false;
    }

    private static bool InheritsByName(INamedTypeSymbol type, string name)
    {
        var current = type.BaseType;
        while (current is not null)
        {
            if (current.Name == name) return true;
            current = current.BaseType;
        }
        foreach (var iface in type.AllInterfaces)
            if (iface.Name == name) return true;
        return false;
    }
}

/// <summary>
/// Maps assembly names to section names. Two rules, both purely structural:
/// <list type="number">
///   <item><c>&lt;X&gt;.Contracts</c> folds into <c>&lt;X&gt;</c> — a contracts assembly is the
///         published face of its section, not a section of its own.</item>
///   <item>The dot-segment prefix shared by every assembly in the solution is stripped for display,
///         so <c>Humans.Store</c> reports as <c>Store</c>. When stripping would leave nothing (the
///         monolith assembly that IS the prefix), the last segment is kept.</item>
/// </list>
/// </summary>
public static class AssemblySections
{
    private const string ContractsSuffix = ".Contracts";

    /// <summary>
    /// Assembly name -> section name, for the whole analyzed assembly set at once (the shared
    /// prefix is a property of the set, not of one name). Pass only assemblies that declare
    /// types — an empty docs/tooling project would otherwise erase the shared prefix.
    /// </summary>
    public static Dictionary<string, string> Resolve(IEnumerable<string> assemblyNames)
    {
        var folded = assemblyNames
            .Where(n => !string.IsNullOrEmpty(n))
            .Distinct(StringComparer.Ordinal)
            .ToDictionary(n => n, Fold, StringComparer.Ordinal);
        var segmented = folded.Values.Distinct(StringComparer.Ordinal).Select(v => v.Split('.')).ToList();

        int shared = segmented.Count == 0 ? 0 : segmented[0].Length;
        foreach (var segments in segmented.Skip(1))
            shared = Math.Min(shared, CommonLeadingSegments(segmented[0], segments));

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, foldedName) in folded)
        {
            var segments = foldedName.Split('.');
            result[name] = shared < segments.Length
                ? string.Join('.', segments.Skip(shared))
                : segments[^1];
        }

        // Stripping the shared prefix can land two DIFFERENT assemblies on one section name —
        // e.g. `Company.Product` (shared consumes it entirely, so it falls back to its last
        // segment) and `Company.Product.Product` (strips to its tail) both yield `Product`.
        // The grouping dictionaries downstream are case-insensitive, so that would silently
        // merge two unrelated assemblies into one section and pool their scores. Keyed on the
        // FOLDED name, so the intended `X` + `X.Contracts` collapse is left alone.
        var collided = result
            .GroupBy(pair => pair.Value, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Select(pair => folded[pair.Key]).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
            .SelectMany(g => g.Select(pair => pair.Key))
            .ToList();
        foreach (var name in collided)
            result[name] = folded[name];

        return result;
    }

    /// <summary>
    /// Whether the assembly is a section's published-contracts assembly (<c>&lt;X&gt;.Contracts</c>) —
    /// the one place a section's exported read API can live outside its own assembly.
    /// </summary>
    public static bool IsContractsAssembly(string assemblyName) =>
        assemblyName.EndsWith(ContractsSuffix, StringComparison.OrdinalIgnoreCase)
        && assemblyName.Length > ContractsSuffix.Length;

    private static string Fold(string assemblyName) =>
        IsContractsAssembly(assemblyName) ? assemblyName[..^ContractsSuffix.Length] : assemblyName;

    private static int CommonLeadingSegments(string[] a, string[] b)
    {
        int i = 0;
        while (i < a.Length && i < b.Length && string.Equals(a[i], b[i], StringComparison.OrdinalIgnoreCase)) i++;
        return i;
    }
}

/// <summary>
/// Effective accessibility: whether a declaration crosses its own assembly boundary, i.e.
/// whether another section can call it. A section is an assembly, so "public surface" is not a
/// judgement call — it is what the assembly exports.
/// </summary>
/// <remarks>
/// A declaration is exported only if it is <c>public</c> <b>and</b> every type containing it is,
/// walked out to the outermost declaration: a <c>public</c> method on an <c>internal</c> class is
/// internal, and so is a <c>public</c> type nested in one. Two deliberate exclusions:
/// <list type="bullet">
///   <item><c>protected</c> is not treated as exported. It is reachable only by deriving, and the
///         scoring passes have always required <see cref="Accessibility.Public"/> on members;
///         admitting protected types while still skipping protected members would be incoherent.</item>
///   <item><c>InternalsVisibleTo</c> is ignored. A test project or analyzer seeing internals does
///         not make them product surface — nothing ships against them.</item>
/// </list>
/// This gates the rules that charge for a <b>declaration's published shape</b>. Rules that charge
/// for a <b>use</b> — cross-section dependencies, duplicate DbSet ownership, DI registration — are
/// deliberately NOT gated: marking a consumer <c>internal</c> does not remove the assembly
/// reference, and the call still crosses the boundary. Gating those would have made coupling free.
/// </remarks>
public static class SurfaceVisibility
{
    public static bool IsExported(ISymbol symbol)
    {
        for (ISymbol? s = symbol; s is not null; s = s.ContainingType)
            if (s.DeclaredAccessibility != Accessibility.Public) return false;
        return true;
    }
}

/// <summary>A source type with its resolved section group, role tags, and primary location.</summary>
public sealed record ClassifiedType(
    INamedTypeSymbol Type,
    string Group,
    HashSet<string> Tags,
    string File,
    Location PrimaryLocation)
{
    public int Line => PrimaryLocation.GetLineSpan().StartLinePosition.Line + 1;

    /// <summary>
    /// Whether this type is visible outside its declaring assembly — see <see cref="SurfaceVisibility"/>.
    /// Internal types stay in the corpus (their implementation still counts toward the
    /// internal-complexity axis) but score nothing on the surface axis.
    /// </summary>
    public bool IsExported => SurfaceVisibility.IsExported(Type);
}
