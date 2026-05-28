using Microsoft.CodeAnalysis;

namespace Reforge;

/// <summary>
/// The shared type-classification pass: opens each non-test project, enumerates source-declared
/// types, resolves each into a section (group) and assigns role tags. Extracted from
/// SurfaceScoreEngine so surface-score, section-shape, and the baseline gate share one pass.
/// </summary>
public static class SolutionClassifier
{
    public static async Task<IReadOnlyList<ClassifiedType>> ClassifyAsync(
        Solution solution, SurfaceScoreConfig config, string solutionDirectory, CancellationToken ct)
    {
        var classified = new List<ClassifiedType>();
        var seenByDisplay = new HashSet<string>(StringComparer.Ordinal);

        foreach (var project in solution.Projects)
        {
            if (project.Name.Contains("Test", StringComparison.OrdinalIgnoreCase)) continue;
            var compilation = await project.GetCompilationAsync(ct);
            if (compilation is null) continue;

            foreach (var type in EnumerateTypes(compilation.GlobalNamespace))
            {
                if (!type.Locations.Any(l => l.IsInSource)) continue;
                if (type.IsImplicitlyDeclared) continue;
                if (type.DeclaredAccessibility == Accessibility.Private) continue;
                if (!seenByDisplay.Add(type.ToDisplayString())) continue;

                var primaryLocation = type.Locations.First(l => l.IsInSource);
                var filePath = primaryLocation.SourceTree?.FilePath ?? "";
                var relPath = LocationHelper.NormalizePath(filePath, solutionDirectory);
                var nsName = type.ContainingNamespace?.ToDisplayString() ?? "";

                var (group, sectionMatch) = ResolveSection(config, type, relPath, nsName);
                var tags = Classify(config, type, relPath, nsName);

                if (sectionMatch?.MatchKind == SectionMatchKind.RepositoryInterface)
                {
                    tags.Add("repositoryInterface");
                    tags.Remove("fullServiceInterface");
                    tags.Remove("readServiceInterface");
                }
                else if (sectionMatch?.MatchKind == SectionMatchKind.ReadServiceInterface)
                {
                    tags.Add("readServiceInterface");
                    tags.Remove("fullServiceInterface");
                    tags.Remove("repositoryInterface");
                }
                else if (sectionMatch?.MatchKind == SectionMatchKind.ServiceInterface)
                {
                    tags.Add("fullServiceInterface");
                    tags.Remove("readServiceInterface");
                    tags.Remove("repositoryInterface");
                }

                classified.Add(new ClassifiedType(type, group, tags, relPath, primaryLocation));
            }
        }

        return classified;
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

    private static (string Group, SectionMatchResult? MatchResult) ResolveSection(
        SurfaceScoreConfig config, INamedTypeSymbol type, string filePath, string namespaceName)
    {
        foreach (var rule in config.EffectiveSections)
        {
            if (rule.RepositoryInterfaces.Contains(type.Name, StringComparer.Ordinal))
                return (rule.Name, new SectionMatchResult(SectionMatchKind.RepositoryInterface));
            if (rule.ReadServiceInterfaces.Contains(type.Name, StringComparer.Ordinal))
                return (rule.Name, new SectionMatchResult(SectionMatchKind.ReadServiceInterface));
            if (rule.ServiceInterfaces.Contains(type.Name, StringComparer.Ordinal))
                return (rule.Name, new SectionMatchResult(SectionMatchKind.ServiceInterface));

            foreach (var p in rule.Paths)
                if (GlobMatcher.MatchesPath(filePath, p))
                    return (rule.Name, new SectionMatchResult(SectionMatchKind.Path));
            foreach (var n in rule.Namespaces)
                if (namespaceName.StartsWith(n, StringComparison.Ordinal))
                    return (rule.Name, new SectionMatchResult(SectionMatchKind.Namespace));
            foreach (var s in rule.Symbols)
                if (GlobMatcher.MatchesName(type.Name, s))
                    return (rule.Name, new SectionMatchResult(SectionMatchKind.Symbol));
        }

        if (config.GroupByNamespaceFallback && !string.IsNullOrEmpty(namespaceName))
        {
            var parts = namespaceName.Split('.');
            if (parts.Length >= 3) return (parts[2], null);
            if (parts.Length >= 2) return (parts[1], null);
            return (parts[0], null);
        }

        return ("(ungrouped)", null);
    }

    internal enum SectionMatchKind { Path, Namespace, Symbol, RepositoryInterface, ServiceInterface, ReadServiceInterface }
    internal sealed record SectionMatchResult(SectionMatchKind MatchKind);

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
