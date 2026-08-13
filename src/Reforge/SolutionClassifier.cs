using Microsoft.CodeAnalysis;

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

        // Pass 2 — name the sections. Only type-declaring assemblies participate: a docs/tooling
        // project that ships no C# is not a section, and letting it into the prefix calculation
        // would strip nothing at all from the real ones.
        var sectionByAssembly = AssemblySections.Resolve(collected.Select(c => c.Assembly));

        return collected
            .Select(c => new ClassifiedType(c.Type, sectionByAssembly[c.Assembly], c.Tags, c.File, c.Location))
            .ToList();
    }

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

    private static string Fold(string assemblyName) =>
        assemblyName.EndsWith(ContractsSuffix, StringComparison.OrdinalIgnoreCase)
        && assemblyName.Length > ContractsSuffix.Length
            ? assemblyName[..^ContractsSuffix.Length]
            : assemblyName;

    private static int CommonLeadingSegments(string[] a, string[] b)
    {
        int i = 0;
        while (i < a.Length && i < b.Length && string.Equals(a[i], b[i], StringComparison.OrdinalIgnoreCase)) i++;
        return i;
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
}
