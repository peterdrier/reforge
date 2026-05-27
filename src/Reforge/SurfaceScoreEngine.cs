using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Reforge;

/// <summary>
/// Single point on the score: the rule that fired, the symbol it fired against,
/// and the points it contributed. Surfaced in the Markdown report's top-offenders
/// section so the reader can act on the score.
/// </summary>
public sealed record ScoreEntry(
    string Rule,
    int Points,
    string Symbol,
    string Group,
    string File,
    int Line,
    string? Detail);

public sealed class GroupScore
{
    public string Name { get; init; } = "";
    public int Total { get; set; }
    public Dictionary<string, int> ByRule { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<ScoreEntry> Entries { get; } = new();
}

public sealed class ScoreReport
{
    public int Total { get; set; }
    public Dictionary<string, GroupScore> Groups { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, int> ByRule { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> DuplicateOwners { get; } = new();
    public List<ScoreDiagnostic> Diagnostics { get; } = new();
    public string? ConfigPath { get; set; }
    public int TypesAnalyzed { get; set; }
    /// <summary>Configured section names — used by --list-groups and the missing-section diagnostic.</summary>
    public List<string> ConfiguredSections { get; } = new();
}

public sealed record ScoreDiagnostic(string Level, string Code, string Message);

/// <summary>
/// Computes a Surface Score for a Roslyn <see cref="Solution"/> using the supplied
/// <see cref="SurfaceScoreConfig"/>. The engine is intentionally generic: domain
/// knowledge enters only through config (group rules, classifications, weights,
/// DbSet ownership). Three scoring passes — durable surface, dependency use,
/// internal shape — accumulate into a single per-group total.
/// </summary>
public sealed class SurfaceScoreEngine
{
    private readonly SurfaceScoreConfig _config;
    private readonly string _solutionDirectory;

    public SurfaceScoreEngine(SurfaceScoreConfig config, string solutionDirectory)
    {
        _config = config;
        _solutionDirectory = solutionDirectory;
    }

    public async Task<ScoreReport> ScoreAsync(Solution solution, CancellationToken ct)
    {
        var report = new ScoreReport();
        report.ConfiguredSections.AddRange(_config.EffectiveSections.Select(s => s.Name));
        var classified = new List<ClassifiedType>();
        // ToDisplayString is the practical cross-compilation identity (SymbolEqualityComparer
        // doesn't match symbols from different project compilations). A type that lives in a
        // shared assembly is seen once per consuming project; without this dedup it would
        // appear N times in `classified` and crash any downstream ToDictionary keyed by it.
        var seenByDisplay = new HashSet<string>(StringComparer.Ordinal);

        foreach (var project in solution.Projects)
        {
            if (project.Name.Contains("Test", StringComparison.OrdinalIgnoreCase))
                continue;

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
                var relPath = LocationHelper.NormalizePath(filePath, _solutionDirectory);
                var nsName = type.ContainingNamespace?.ToDisplayString() ?? "";

                var (group, sectionMatch) = ResolveSection(type, relPath, nsName);
                var tags = Classify(type, relPath, nsName);

                // Auto-classify types named in a section's RepositoryInterfaces / ServiceInterfaces /
                // ReadServiceInterfaces lists. This is sugar: the user doesn't have to write a
                // matching classification entry — and these lists are usually the most accurate
                // (hand-curated) signal we have about what role each interface plays.
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

        report.TypesAnalyzed = classified.Count;

        // Single canonical index — built once, used by both dependency-use and DI-registration
        // passes. Safe under duplicate ToDisplayString because `classified` is already deduped.
        var typesByDisplay = classified.ToDictionary(c => c.Type.ToDisplayString(), c => c, StringComparer.Ordinal);

        // Pass 1 — durable surface
        ScoreDurableSurface(classified, report);

        // Pass 2 — dependency use
        ScoreDependencyUse(classified, typesByDisplay, report);

        // Pass 3 — internal shape
        await ScoreInternalShape(classified, report, ct);

        // Pass 4 — return-type rules (canonical-DTO credit + entity-across-section penalty).
        ScoreReturnTypeRules(classified, typesByDisplay, report);

        // Pass 5 — write-capable interface used read-only. Needs the semantic model and
        // is the most expensive pass, so it runs last.
        await ScoreWriteCapableUsedReadOnlyAsync(classified, typesByDisplay, solution, report, ct);

        // Cross-cutting: duplicate DbSet owners (resource ownership), DI registrations,
        // one-implementation interfaces.
        ScoreDuplicateDbSetOwners(classified, solution, report, ct);
        await ScoreDiRegistrationsAsync(solution, typesByDisplay, report, ct);
        ScoreOneImplementationInterfaces(classified, report);

        return report;
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

    private (string Group, SectionMatchResult? MatchResult) ResolveSection(
        INamedTypeSymbol type, string filePath, string namespaceName)
    {
        foreach (var rule in _config.EffectiveSections)
        {
            // Exact-name lists first — they're the most precise signal and need to win even
            // when a less-specific Symbols pattern from another section would match.
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

        if (_config.GroupByNamespaceFallback && !string.IsNullOrEmpty(namespaceName))
        {
            // Heuristic: top-level segment after the assembly/root namespace is the section.
            // e.g. "MyApp.Application.Tickets.Services" -> "Tickets" when there are 3+ parts;
            // shorter namespaces fall back to the deepest segment.
            var parts = namespaceName.Split('.');
            if (parts.Length >= 3) return (parts[2], null);
            if (parts.Length >= 2) return (parts[1], null);
            return (parts[0], null);
        }

        return ("(ungrouped)", null);
    }

    private enum SectionMatchKind { Path, Namespace, Symbol, RepositoryInterface, ServiceInterface, ReadServiceInterface }
    private sealed record SectionMatchResult(SectionMatchKind MatchKind);

    private HashSet<string> Classify(INamedTypeSymbol type, string filePath, string namespaceName)
    {
        var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (name, rule) in _config.Classifications)
        {
            if (Matches(rule, type, filePath, namespaceName))
                tags.Add(name);
        }

        // Resolve precedence: a concrete *Repository class also matches *Service via the
        // generic defaults. Repository wins over applicationService; interface tags win
        // over implementation tags on interfaces.
        if (type.TypeKind == TypeKind.Interface)
        {
            tags.Remove("repositoryImplementation");
            tags.Remove("applicationService");
            tags.Remove("controller");
            tags.Remove("backgroundJob");
            // Read-service interface is a refinement of full-service; keep only the more specific tag.
            if (tags.Contains("readServiceInterface"))
                tags.Remove("fullServiceInterface");
            if (tags.Contains("repositoryInterface"))
                tags.Remove("fullServiceInterface");
        }
        else
        {
            tags.Remove("readServiceInterface");
            tags.Remove("fullServiceInterface");
            tags.Remove("repositoryInterface");
            if (tags.Contains("repositoryImplementation"))
                tags.Remove("applicationService");
        }

        return tags;
    }

    private static bool Matches(ClassificationRule rule, INamedTypeSymbol type, string filePath, string namespaceName)
    {
        // Any single criterion matching is enough — rules are inclusive disjunctions.
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

    // ---------------- Pass 1: Durable Surface ----------------

    private void ScoreDurableSurface(List<ClassifiedType> classified, ScoreReport report)
    {
        foreach (var c in classified)
        {
            if (c.Tags.Contains("dto") && LooksLikeDataCarrier(c.Type))
                ScoreDtoSurface(c, report);

            if (c.Tags.Contains("readServiceInterface"))
                ScoreInterfaceMethods(c, "readServiceInterfaceMethod", report);
            else if (c.Tags.Contains("fullServiceInterface"))
                ScoreInterfaceMethods(c, "fullServiceInterfaceMethod", report);
            else if (c.Tags.Contains("repositoryInterface"))
            {
                AddEntry(report, c.Group, "newRepositoryInterface",
                    _config.Weight("newRepositoryInterface"), c.Type, c.File, c.Line, null);
                ScoreInterfaceMethods(c, "repositoryInterfaceMethod", report);
            }
            else if (c.Tags.Contains("repositoryImplementation"))
            {
                AddEntry(report, c.Group, "newRepositoryImplementation",
                    _config.Weight("newRepositoryImplementation"), c.Type, c.File, c.Line, null);
                ScorePublicMethods(c, "repositoryImplementationMethod", report);
            }
            else if (c.Tags.Contains("controller"))
                ScoreControllerActions(c, report);
            else if (c.Tags.Contains("backgroundJob"))
                AddEntry(report, c.Group, "backgroundJob",
                    _config.Weight("backgroundJob"), c.Type, c.File, c.Line, null);
            else if (c.Tags.Contains("applicationService"))
                ScorePublicMethods(c, "applicationServiceMethod", report);
        }
    }

    private void ScoreDtoSurface(ClassifiedType c, ScoreReport report)
    {
        AddEntry(report, c.Group, "publicDtoType", _config.Weight("publicDtoType"), c.Type, c.File, c.Line, null);

        foreach (var member in c.Type.GetMembers())
        {
            if (member is not IPropertySymbol prop) continue;
            if (prop.DeclaredAccessibility != Accessibility.Public) continue;

            var loc = prop.Locations.FirstOrDefault(l => l.IsInSource);
            var (file, line) = LocateMember(loc, c);

            var (rule, weight) = ClassifyDtoProperty(prop);
            AddEntry(report, c.Group, rule, weight, prop, file, line, prop.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat));
        }
    }

    private (string Rule, int Weight) ClassifyDtoProperty(IPropertySymbol prop)
    {
        var t = prop.Type;
        if (IsCollectionType(t))
            return ("dtoCollectionProperty", _config.Weight("dtoCollectionProperty"));
        if (IsNestedDtoType(t))
            return ("dtoNestedProperty", _config.Weight("dtoNestedProperty"));
        return ("dtoScalarProperty", _config.Weight("dtoScalarProperty"));
    }

    private static bool IsCollectionType(ITypeSymbol t)
    {
        if (t is IArrayTypeSymbol) return true;
        if (t is INamedTypeSymbol n)
        {
            var n2 = n.OriginalDefinition.ToDisplayString();
            return n2.StartsWith("System.Collections.Generic.", StringComparison.Ordinal)
                || n2 == "System.Collections.IEnumerable"
                || n2.StartsWith("System.Collections.Immutable.", StringComparison.Ordinal);
        }
        return false;
    }

    private static bool IsNestedDtoType(ITypeSymbol t)
    {
        if (t.SpecialType != SpecialType.None) return false;
        if (t.TypeKind != TypeKind.Class && t.TypeKind != TypeKind.Struct) return false;
        if (t.ContainingNamespace?.ToDisplayString().StartsWith("System", StringComparison.Ordinal) == true) return false;
        if (!t.Locations.Any(l => l.IsInSource)) return false;
        return true;
    }

    /// <summary>
    /// A type only counts as a DTO if it actually carries data: public properties and
    /// no business-logic methods. This prevents static command-registration classes,
    /// service classes that happen to match a name pattern, etc. from inflating the DTO score.
    /// </summary>
    private static bool LooksLikeDataCarrier(INamedTypeSymbol type)
    {
        if (type.IsStatic) return false;
        if (type.TypeKind is not (TypeKind.Class or TypeKind.Struct)) return false;

        int publicProps = 0;
        int publicMethods = 0;
        foreach (var m in type.GetMembers())
        {
            if (m.IsImplicitlyDeclared) continue;
            if (m.DeclaredAccessibility != Accessibility.Public) continue;

            switch (m)
            {
                case IPropertySymbol:
                    publicProps++;
                    break;
                case IMethodSymbol method
                    when method.MethodKind == MethodKind.Ordinary
                      && method.AssociatedSymbol is null:
                    publicMethods++;
                    break;
            }
        }
        return publicProps >= 1 && publicMethods == 0;
    }

    private void ScoreInterfaceMethods(ClassifiedType c, string ruleKey, ScoreReport report)
    {
        var weight = _config.Weight(ruleKey);
        foreach (var member in c.Type.GetMembers())
        {
            if (member is not IMethodSymbol m) continue;
            if (m.MethodKind != MethodKind.Ordinary) continue;
            if (m.AssociatedSymbol is not null) continue;
            if (m.IsImplicitlyDeclared) continue;

            var loc = m.Locations.FirstOrDefault(l => l.IsInSource);
            var (file, line) = LocateMember(loc, c);
            AddEntry(report, c.Group, ruleKey, weight, m, file, line, m.Name);
        }
    }

    private void ScorePublicMethods(ClassifiedType c, string ruleKey, ScoreReport report)
    {
        var weight = _config.Weight(ruleKey);
        foreach (var member in c.Type.GetMembers())
        {
            if (member is not IMethodSymbol m) continue;
            if (m.MethodKind != MethodKind.Ordinary) continue;
            if (m.AssociatedSymbol is not null) continue;
            if (m.IsImplicitlyDeclared) continue;
            if (m.DeclaredAccessibility != Accessibility.Public) continue;

            var loc = m.Locations.FirstOrDefault(l => l.IsInSource);
            var (file, line) = LocateMember(loc, c);
            AddEntry(report, c.Group, ruleKey, weight, m, file, line, m.Name);
        }
    }

    private void ScoreControllerActions(ClassifiedType c, ScoreReport report)
    {
        var weight = _config.Weight("controllerAction");
        foreach (var member in c.Type.GetMembers())
        {
            if (member is not IMethodSymbol m) continue;
            if (m.MethodKind != MethodKind.Ordinary) continue;
            if (m.DeclaredAccessibility != Accessibility.Public) continue;
            if (m.IsImplicitlyDeclared) continue;

            var loc = m.Locations.FirstOrDefault(l => l.IsInSource);
            var (file, line) = LocateMember(loc, c);
            AddEntry(report, c.Group, "controllerAction", weight, m, file, line, m.Name);
        }
    }

    // ---------------- Pass 2: Dependency Use ----------------

    private void ScoreDependencyUse(
        List<ClassifiedType> classified,
        Dictionary<string, ClassifiedType> typesByDisplay,
        ScoreReport report)
    {
        foreach (var c in classified)
        {
            if (c.Type.TypeKind != TypeKind.Class) continue;

            foreach (var ctor in c.Type.Constructors)
            {
                if (ctor.IsImplicitlyDeclared) continue;
                foreach (var param in ctor.Parameters)
                {
                    var depDisplay = param.Type.ToDisplayString();
                    if (!typesByDisplay.TryGetValue(depDisplay, out var dep)) continue;

                    // Same-group dependencies don't cost anything (or are zero-weighted).
                    var sameGroup = string.Equals(dep.Group, c.Group, StringComparison.OrdinalIgnoreCase);

                    string? rule = null;
                    if (dep.Tags.Contains("repositoryInterface") || dep.Tags.Contains("repositoryImplementation"))
                        rule = sameGroup ? null : "crossSectionRepository";
                    else if (dep.Tags.Contains("readServiceInterface"))
                        rule = sameGroup ? "sameSectionReadService" : "crossSectionReadInterface";
                    else if (dep.Tags.Contains("fullServiceInterface") || dep.Tags.Contains("applicationService"))
                        rule = sameGroup ? null : "crossSectionFullService";

                    if (rule is null) continue;
                    var weight = _config.Weight(rule);
                    if (weight == 0) continue;

                    var loc = ctor.Locations.FirstOrDefault(l => l.IsInSource);
                    var (file, line) = LocateMember(loc, c);
                    AddEntry(report, c.Group, rule, weight, c.Type, file, line,
                        $"{c.Type.Name} <- {param.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}");
                }
            }
        }
    }

    // ---------------- Pass 3: Internal Shape ----------------

    private async Task ScoreInternalShape(List<ClassifiedType> classified, ScoreReport report, CancellationToken ct)
    {
        var overflowWeight = _config.Weight("methodParameterOverflow");
        var boolWeight = _config.Weight("booleanParameter");
        var tupleWeight = _config.Weight("tupleReturn");
        var optionsBagWeight = _config.Weight("optionsBag");
        var nameWeight = _config.Weight("dashboardAdminPageName");

        foreach (var c in classified)
        {
            // Only score shape for code-bearing types (skip pure DTOs).
            if (c.Tags.Contains("dto") && !c.Tags.Contains("applicationService")) continue;

            foreach (var member in c.Type.GetMembers())
            {
                if (member is not IMethodSymbol m) continue;
                if (m.MethodKind != MethodKind.Ordinary) continue;
                if (m.AssociatedSymbol is not null) continue;
                if (m.IsImplicitlyDeclared) continue;
                if (m.DeclaredAccessibility != Accessibility.Public) continue;

                var loc = m.Locations.FirstOrDefault(l => l.IsInSource);
                var (file, line) = LocateMember(loc, c);

                // Param-count overflow (after 2).
                if (m.Parameters.Length > 2 && overflowWeight > 0)
                {
                    var extra = m.Parameters.Length - 2;
                    AddEntry(report, c.Group, "methodParameterOverflow", extra * overflowWeight, m, file, line,
                        $"{m.Name}({m.Parameters.Length} params)");
                }

                // Bool parameters.
                foreach (var p in m.Parameters)
                {
                    if (p.Type.SpecialType == SpecialType.System_Boolean && boolWeight > 0)
                        AddEntry(report, c.Group, "booleanParameter", boolWeight, m, file, line, $"{m.Name}({p.Name})");
                }

                // Tuple return.
                if (tupleWeight > 0 && IsTupleType(UnwrapTaskLike(m.ReturnType)))
                    AddEntry(report, c.Group, "tupleReturn", tupleWeight, m, file, line, m.Name);

                // Options-bag: single param whose type carries many unrelated public properties.
                if (optionsBagWeight > 0 && m.Parameters.Length == 1 && IsOptionsBag(m.Parameters[0].Type))
                    AddEntry(report, c.Group, "optionsBag", optionsBagWeight, m, file, line, m.Name);

                // Naming smell: ForDashboard / ForAdmin / ForPage / Dashboard / Admin in the method name.
                if (nameWeight > 0 && HasDashboardAdminPageName(m.Name))
                    AddEntry(report, c.Group, "dashboardAdminPageName", nameWeight, m, file, line, m.Name);
            }
        }

        await Task.CompletedTask;
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

    private static bool IsTupleType(ITypeSymbol t)
    {
        if (t is INamedTypeSymbol n && n.IsTupleType) return true;
        return false;
    }

    private static bool IsOptionsBag(ITypeSymbol t)
    {
        if (t is not INamedTypeSymbol n) return false;
        if (n.SpecialType != SpecialType.None) return false;
        if (n.ContainingNamespace?.ToDisplayString().StartsWith("System", StringComparison.Ordinal) == true) return false;
        // Framework / third-party types (Roslyn ITypeSymbol, MediatR commands, etc.) shouldn't
        // count — only flag the user's own types.
        if (!n.Locations.Any(l => l.IsInSource)) return false;
        var publicProps = n.GetMembers().OfType<IPropertySymbol>()
            .Count(p => p.DeclaredAccessibility == Accessibility.Public);
        return publicProps >= 4 && n.Name.EndsWith("Options", StringComparison.Ordinal)
            || publicProps >= 6;
    }

    private static bool HasDashboardAdminPageName(string name)
    {
        return name.Contains("Dashboard", StringComparison.Ordinal)
            || name.Contains("ForAdmin", StringComparison.Ordinal)
            || name.Contains("ForPage", StringComparison.Ordinal)
            || name.Contains("AdminPage", StringComparison.Ordinal);
    }

    // ---------------- Pass 4: Return-type rules ----------------

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

        // Index canonical DTO names across all sections — a Tickets method returning Users's
        // canonical DTO still earns the credit.
        var canonicalNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in _config.EffectiveSections)
            foreach (var n in s.CanonicalReadDtos)
                canonicalNames.Add(n);

        foreach (var c in classified)
        {
            if (c.Type.TypeKind != TypeKind.Class) continue;
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

                // Canonical DTO credit takes precedence — exempt from the entity penalty.
                if (canonicalWeight != 0 && canonicalNames.Contains(named.Name))
                {
                    AddEntry(report, c.Group, "canonicalReadDtoReturn", canonicalWeight, m, file, line,
                        $"{m.Name} -> {named.Name}");
                    continue;
                }

                if (entityWeight == 0) continue;
                if (!typesByDisplay.TryGetValue(named.ToDisplayString(), out var returnTypeInfo)) continue;
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

    // ---------------- Pass 5: write-capable interface used read-only ----------------

    /// <summary>
    /// Symbol-based (no name-prefix guessing). For each full-service interface paired with a
    /// read-service interface (inheritance, or a sibling named "{Full}Read" in the same
    /// namespace), check every consumer that injects the full interface. If every observed
    /// invocation on that injected dependency targets a method that ALSO exists on the read
    /// interface (same name + arity), the consumer doesn't need write capability and the rule
    /// fires +12 against the consumer's section. A single full-only call cancels the rule.
    /// </summary>
    private async Task ScoreWriteCapableUsedReadOnlyAsync(
        List<ClassifiedType> classified,
        Dictionary<string, ClassifiedType> typesByDisplay,
        Solution solution,
        ScoreReport report,
        CancellationToken ct)
    {
        var weight = _config.Weight("writeCapableInterfaceUsedReadOnly");
        if (weight == 0) return;

        // Build full -> read pairs once. A pair is established when:
        //   - the full interface directly inherits a classified read-service interface, OR
        //   - a classified read-service interface with name "{full.Name}Read" or "Read{stripped}"
        //     lives in the same namespace.
        var pairs = BuildFullToReadPairs(classified, typesByDisplay);
        if (pairs.Count == 0) return;

        // For each full interface, collect the set of method (name, arity) tuples on the read
        // interface. A call on the full interface counts as "read-covered" iff this set
        // contains its target method's (name, arity).
        var readMethodIndex = pairs.ToDictionary(
            kv => kv.Key,
            kv => new HashSet<(string Name, int Arity)>(
                kv.Value.Type.GetMembers().OfType<IMethodSymbol>()
                    .Where(m => m.MethodKind == MethodKind.Ordinary)
                    .Select(m => (m.Name, m.Parameters.Length))),
            StringComparer.Ordinal);

        // Index full interfaces by display string for fast lookup during the syntax walk.
        var fullByDisplay = pairs.Keys.ToHashSet(StringComparer.Ordinal);

        foreach (var c in classified)
        {
            if (c.Type.TypeKind != TypeKind.Class) continue;

            // Find every constructor param whose type is one of our paired full interfaces.
            // For each such injection, we'll walk the class body looking at how this dep is used.
            var injectedFulls = new List<(string FullDisplay, IParameterSymbol Param)>();
            foreach (var ctor in c.Type.Constructors)
            {
                if (ctor.IsImplicitlyDeclared) continue;
                foreach (var p in ctor.Parameters)
                {
                    var d = p.Type.ToDisplayString();
                    if (fullByDisplay.Contains(d))
                        injectedFulls.Add((d, p));
                }
            }
            if (injectedFulls.Count == 0) continue;

            // The class might be partial across files — examine every declaring tree.
            foreach (var declRef in c.Type.DeclaringSyntaxReferences)
            {
                var tree = declRef.SyntaxTree;
                var project = solution.Projects.FirstOrDefault(p => p.Documents.Any(d => d.FilePath == tree.FilePath));
                if (project is null) continue;
                var compilation = await project.GetCompilationAsync(ct);
                if (compilation is null) continue;
                var model = compilation.GetSemanticModel(tree);
                var classNode = await declRef.GetSyntaxAsync(ct);

                foreach (var (fullDisplay, param) in injectedFulls)
                {
                    var readSet = readMethodIndex[fullDisplay];
                    int readCalls = 0;
                    int fullOnlyCalls = 0;

                    foreach (var invocation in classNode.DescendantNodes().OfType<InvocationExpressionSyntax>())
                    {
                        if (invocation.Expression is not MemberAccessExpressionSyntax ma) continue;

                        // Resolve the receiver's type. The injected interface is held by a
                        // field assigned in the ctor; the field's declared type matches the
                        // ctor param's type, so checking against the full display string covers
                        // both `_dep.Foo()` and `dep.Foo()` (constructor-only stash).
                        var receiverType = model.GetTypeInfo(ma.Expression).Type;
                        if (receiverType is null) continue;
                        if (!string.Equals(receiverType.ToDisplayString(), fullDisplay, StringComparison.Ordinal))
                            continue;

                        var methodName = ma.Name.Identifier.Text;
                        var arity = invocation.ArgumentList?.Arguments.Count ?? 0;
                        // C# allows omitting optional args, so we can't insist on exact arity.
                        // Accept a read-cover match if ANY read-interface method has the same name.
                        // (Overloads are rare in practice; this loosens the check enough to
                        // handle default arguments while staying symbol-grounded.)
                        if (readSet.Any(t => t.Name == methodName))
                            readCalls++;
                        else
                            fullOnlyCalls++;
                    }

                    if (fullOnlyCalls == 0 && readCalls > 0)
                    {
                        var readName = pairs[fullDisplay].Type.Name;
                        var fullName = param.Type.Name;
                        var loc = param.Locations.FirstOrDefault(l => l.IsInSource)
                            ?? c.PrimaryLocation;
                        var (file, line) = LocateMember(loc, c);
                        AddEntry(report, c.Group, "writeCapableInterfaceUsedReadOnly", weight, c.Type, file, line,
                            $"{c.Type.Name} <- {fullName} (use {readName} instead; {readCalls} read calls, 0 write calls)");
                    }
                }
            }
        }
    }

    private Dictionary<string, ClassifiedType> BuildFullToReadPairs(
        List<ClassifiedType> classified,
        Dictionary<string, ClassifiedType> typesByDisplay)
    {
        var pairs = new Dictionary<string, ClassifiedType>(StringComparer.Ordinal);

        var fullInterfaces = classified.Where(c =>
            c.Type.TypeKind == TypeKind.Interface && c.Tags.Contains("fullServiceInterface")).ToList();
        var readInterfaces = classified.Where(c =>
            c.Type.TypeKind == TypeKind.Interface && c.Tags.Contains("readServiceInterface")).ToList();
        if (fullInterfaces.Count == 0 || readInterfaces.Count == 0) return pairs;

        var readByDisplay = readInterfaces.ToDictionary(r => r.Type.ToDisplayString(), r => r, StringComparer.Ordinal);
        var readByNameInNamespace = readInterfaces.ToLookup(
            r => $"{r.Type.ContainingNamespace?.ToDisplayString()}|{r.Type.Name}",
            StringComparer.Ordinal);

        foreach (var full in fullInterfaces)
        {
            // Strategy 1: direct inheritance. The full interface lists the read interface as a base.
            var inheritedRead = full.Type.Interfaces.FirstOrDefault(i =>
                readByDisplay.ContainsKey(i.ToDisplayString()));
            if (inheritedRead is not null)
            {
                pairs[full.Type.ToDisplayString()] = readByDisplay[inheritedRead.ToDisplayString()];
                continue;
            }

            // Strategy 2: same-namespace sibling named "{full.Name}Read" — e.g. IUserService
            // pairs with IUserServiceRead in the same namespace. This catches the common
            // Humans-style layout where read and full are siblings rather than parent-child.
            var ns = full.Type.ContainingNamespace?.ToDisplayString() ?? "";
            var siblingKey = $"{ns}|{full.Type.Name}Read";
            var sibling = readByNameInNamespace[siblingKey].FirstOrDefault();
            if (sibling is not null)
            {
                pairs[full.Type.ToDisplayString()] = sibling;
            }
        }

        return pairs;
    }

    // ---------------- Cross-cutting: duplicate DbSet owners ----------------

    private void ScoreDuplicateDbSetOwners(
        List<ClassifiedType> classified,
        Solution solution,
        ScoreReport report,
        CancellationToken ct)
    {
        var ownerMap = _config.Resources.DbSets.OwnerByName;
        if (ownerMap.Count == 0) return;

        // For each repository implementation, collect which DbSets it touches, then flag
        // any DbSet read or written by a class outside the configured owning group.
        var weight = _config.Weight("duplicateDbSetOwner");
        var alreadyReported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var c in classified)
        {
            if (c.Type.TypeKind != TypeKind.Class) continue;

            // Cheap pre-filter: only classes with a DbContext constructor param touch DbSets.
            var hasDbContext = c.Type.Constructors.Any(ctor =>
                ctor.Parameters.Any(p => DbContextAnalyzer.IsDbContextType(p.Type)));
            if (!hasDbContext) continue;

            var accesses = DbContextAnalyzer.FindDbSetAccessesAsync(c.Type, solution, ct).GetAwaiter().GetResult();
            var distinct = accesses.Select(a => a.DbSetName).Distinct(StringComparer.OrdinalIgnoreCase);

            foreach (var dbSet in distinct)
            {
                if (!ownerMap.TryGetValue(dbSet, out var owner)) continue;
                if (string.Equals(owner, c.Group, StringComparison.OrdinalIgnoreCase)) continue;

                var key = $"{c.Type.ToDisplayString()}|{dbSet}";
                if (!alreadyReported.Add(key)) continue;

                AddEntry(report, c.Group, "duplicateDbSetOwner", weight, c.Type, c.File, c.Line,
                    $"{c.Type.Name} touches {dbSet} (owned by {owner})");
                report.DuplicateOwners.Add($"{c.Type.ToDisplayString()} touches {dbSet} (owner: {owner})");
            }
        }
    }

    private async Task ScoreDiRegistrationsAsync(
        Solution solution,
        Dictionary<string, ClassifiedType> classifiedByDisplay,
        ScoreReport report,
        CancellationToken ct)
    {
        var weight = _config.Weight("diRegistration");
        if (weight == 0) return;

        // Heuristic: a DI registration is a call to AddSingleton/AddScoped/AddTransient.
        // We attribute each registration to the group of the *registered service* (the
        // first type argument) so that "Tickets registers TicketsService" credits Tickets.
        var registrationMethods = new HashSet<string>(StringComparer.Ordinal)
        {
            "AddSingleton", "AddScoped", "AddTransient", "TryAddSingleton", "TryAddScoped", "TryAddTransient"
        };

        foreach (var project in solution.Projects)
        {
            if (project.Name.Contains("Test", StringComparison.OrdinalIgnoreCase)) continue;
            var compilation = await project.GetCompilationAsync(ct);
            if (compilation is null) continue;

            foreach (var tree in compilation.SyntaxTrees)
            {
                var root = await tree.GetRootAsync(ct);
                var model = compilation.GetSemanticModel(tree);

                foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    string? methodName = invocation.Expression switch
                    {
                        MemberAccessExpressionSyntax ma => ma.Name is GenericNameSyntax g ? g.Identifier.Text : ma.Name.Identifier.Text,
                        GenericNameSyntax g => g.Identifier.Text,
                        _ => null
                    };
                    if (methodName is null || !registrationMethods.Contains(methodName)) continue;

                    var genericName = invocation.Expression switch
                    {
                        MemberAccessExpressionSyntax ma2 => ma2.Name as GenericNameSyntax,
                        GenericNameSyntax g2 => g2,
                        _ => null
                    };
                    if (genericName is null || genericName.TypeArgumentList.Arguments.Count == 0) continue;

                    var typeArg = genericName.TypeArgumentList.Arguments[0];
                    var typeInfo = model.GetSymbolInfo(typeArg).Symbol as INamedTypeSymbol;
                    if (typeInfo is null) continue;

                    if (!classifiedByDisplay.TryGetValue(typeInfo.ToDisplayString(), out var c)) continue;

                    var loc = invocation.GetLocation();
                    var ls = loc.GetLineSpan();
                    var file = LocationHelper.NormalizePath(ls.Path, _solutionDirectory);
                    AddEntry(report, c.Group, "diRegistration", weight, typeInfo, file, ls.StartLinePosition.Line + 1,
                        $"{methodName}<{typeInfo.Name}>");
                }
            }
        }
    }

    private void ScoreOneImplementationInterfaces(List<ClassifiedType> classified, ScoreReport report)
    {
        var weight = _config.Weight("oneImplementationInterface");
        if (weight == 0) return;

        // For every classified interface, count how many classified classes implement it.
        var interfaces = classified.Where(c => c.Type.TypeKind == TypeKind.Interface).ToList();
        var classes = classified.Where(c => c.Type.TypeKind == TypeKind.Class).ToList();
        if (interfaces.Count == 0 || classes.Count == 0) return;

        foreach (var iface in interfaces)
        {
            int implCount = 0;
            ClassifiedType? singleImpl = null;
            foreach (var cls in classes)
            {
                if (cls.Type.AllInterfaces.Any(i => SymbolEqualityComparer.Default.Equals(i, iface.Type)))
                {
                    implCount++;
                    singleImpl = cls;
                    if (implCount > 1) break;
                }
            }
            if (implCount == 1 && singleImpl is not null)
            {
                AddEntry(report, iface.Group, "oneImplementationInterface", weight, iface.Type, iface.File, iface.Line,
                    $"only {singleImpl.Type.Name} implements {iface.Type.Name}");
            }
        }
    }

    // ---------------- Helpers ----------------

    private (string File, int Line) LocateMember(Location? loc, ClassifiedType fallback)
    {
        if (loc is null)
            return (fallback.File, fallback.Line);
        var ls = loc.GetLineSpan();
        var file = LocationHelper.NormalizePath(ls.Path, _solutionDirectory);
        return (file, ls.StartLinePosition.Line + 1);
    }

    private static void AddEntry(ScoreReport report, string groupName, string rule, int points,
        ISymbol symbol, string file, int line, string? detail)
    {
        if (points == 0) return;
        if (!report.Groups.TryGetValue(groupName, out var g))
        {
            g = new GroupScore { Name = groupName };
            report.Groups[groupName] = g;
        }
        var entry = new ScoreEntry(rule, points, symbol.Name, groupName, file, line, detail);
        g.Entries.Add(entry);
        g.Total += points;
        g.ByRule[rule] = g.ByRule.GetValueOrDefault(rule) + points;
        report.Total += points;
        report.ByRule[rule] = report.ByRule.GetValueOrDefault(rule) + points;
    }

    private sealed record ClassifiedType(
        INamedTypeSymbol Type,
        string Group,
        HashSet<string> Tags,
        string File,
        Location PrimaryLocation)
    {
        public int Line => PrimaryLocation.GetLineSpan().StartLinePosition.Line + 1;
    }
}
