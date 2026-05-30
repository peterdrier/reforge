using Microsoft.CodeAnalysis;

namespace Reforge;

// ---- Anchor + shape data model (shared by surface-score and the section-shape view) ----

/// <summary>A canonical DTO anchor with its recursive, path-based member inventory.</summary>
public sealed record DtoAnchor(string Display, string Section, string Role, IReadOnlyList<string> Paths);

public sealed record InterfaceAnchorMethod(string Name, string Returns);

/// <summary>A read/full service interface anchor with its declared method signatures.</summary>
public sealed record InterfaceAnchor(string Display, string Section, string Role, IReadOnlyList<InterfaceAnchorMethod> Methods);

public sealed record ShardAnchor(string Name, string Purpose, IReadOnlyList<string> Methods);

/// <summary>
/// A cross-section dependency use: <see cref="Caller"/> in <see cref="CallerSection"/> injects
/// <see cref="Dependency"/> (owned by <see cref="DependencySection"/>). File/Line locate the
/// consumer's constructor parameter for scoring.
/// </summary>
public sealed record CrossSectionUse(
    string Caller, string CallerSection, string Dependency, string DependencySection,
    string? SuggestedReadInterface, IReadOnlyList<string> ObservedCalls)
{
    public string File { get; init; } = "";
    public int Line { get; init; }
}

public sealed record MissingSurface(string Section, string Rule, string Detail);

/// <summary>
/// A read-service-interface method whose return shape is charged (projection/predicate/scalar/UI),
/// not the section's primary Info DTO. Carries the in-memory symbol + source location so the engine
/// can score it with the existing symbol-based <c>AddEntry</c>.
/// </summary>
public sealed record ChargedReadMethod(
    string Interface, string Method, ReadMethodKind Kind, string Returns,
    bool EscapeHatch, string? EscapeHatchReason)
{
    public string File { get; init; } = "";
    public int Line { get; init; }
    public IMethodSymbol? Symbol { get; init; }
}

public sealed record DerivableReadMethod(string Interface, string Method, ReadMethodKind Kind, string TargetDto, string Hint);
public sealed record MissingInfoFact(string Fact, string TargetDto);
public sealed record CacheFactCandidate(string Method, string Fact, string CacheDto);

/// <summary>
/// The resolved structural shape of one configured section: owned repositories, read/full
/// interfaces, primary/settings/cache DTOs (config or convention/inference), documented read
/// shards, cross-section uses, missing surfaces, charged read methods, and advisory candidates.
/// Lists default to empty; later analysis passes fill cross-section/advisory/cache-inference.
/// </summary>
public sealed record SectionShape
{
    public required string Name { get; init; }
    public required SectionFacts Facts { get; init; }
    public IReadOnlyList<string> OwnedRepositoryInterfaces { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> OwnedRepositoryImplementations { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> FullServiceInterfaces { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ReadServiceInterfaces { get; init; } = Array.Empty<string>();
    public DtoAnchor? PrimaryInfoDto { get; init; }
    public DtoAnchor? SettingsInfoDto { get; init; }
    public DtoAnchor? CacheDto { get; init; }
    public string CacheDtoProvenance { get; init; } = "none";
    public IReadOnlyList<ShardAnchor> ReadShards { get; init; } = Array.Empty<ShardAnchor>();
    public IReadOnlyList<CrossSectionUse> ReadSurfaceCallers { get; init; } = Array.Empty<CrossSectionUse>();
    public IReadOnlyList<CrossSectionUse> WriteSurfaceCallers { get; init; } = Array.Empty<CrossSectionUse>();
    public IReadOnlyList<CrossSectionUse> WriteSurfaceUnverified { get; init; } = Array.Empty<CrossSectionUse>();
    public IReadOnlyList<MissingSurface> Missing { get; init; } = Array.Empty<MissingSurface>();
    public IReadOnlyList<GrandfatheredDependency> Grandfathered { get; init; } = Array.Empty<GrandfatheredDependency>();
    public IReadOnlyList<EscapeHatchReadMethod> EscapeHatches { get; init; } = Array.Empty<EscapeHatchReadMethod>();
    public IReadOnlyList<ChargedReadMethod> ChargedReadMethods { get; init; } = Array.Empty<ChargedReadMethod>();
    public IReadOnlyList<DerivableReadMethod> DerivableReadMethods { get; init; } = Array.Empty<DerivableReadMethod>();
    public IReadOnlyList<MissingInfoFact> MissingInfoFacts { get; init; } = Array.Empty<MissingInfoFact>();
    public IReadOnlyList<CacheFactCandidate> CacheFactCandidates { get; init; } = Array.Empty<CacheFactCandidate>();
}

public sealed record SectionArchitecture(
    IReadOnlyList<SectionShape> Sections,
    IReadOnlyList<DtoAnchor> DtoAnchors,
    IReadOnlyList<InterfaceAnchor> InterfaceAnchors,
    IReadOnlyList<ShardAnchor> ShardAnchors);

/// <summary>
/// Resolves the architectural shape of each configured section from the shared
/// <see cref="SolutionClassifier"/> output. Section-architecture rules and the section-shape view
/// both consume these shapes, so the read/full pairing, DTO anchoring, and charged-read
/// classification live in one place. Only configured sections (those with a <see cref="SectionRule"/>)
/// are shaped — namespace-fallback groups have no primary DTO anchor and are skipped.
/// </summary>
public static class SectionShapeAnalyzer
{
    public static Task<SectionArchitecture> AnalyzeAsync(Solution solution,
        List<ClassifiedType> classified, SurfaceScoreConfig config, string solutionDirectory, CancellationToken ct)
    {
        // Which sections own a classified repository (interface or implementation)? Drives RepoBacked.
        var repoSectionNames = new HashSet<string>(
            classified.Where(c => c.Tags.Contains("repositoryInterface") || c.Tags.Contains("repositoryImplementation"))
                      .Select(c => c.Group),
            StringComparer.OrdinalIgnoreCase);

        // Canonical DTO simple names — the set DtoInventory descends into. Behavioral, not
        // nominal: a type counts if it is dto-tagged OR is a pure data carrier (public props,
        // no public methods). The behavioral fallback keeps child-DTO descent working even when
        // the active config carries no "dto" classification rule (e.g. a section-only test config).
        var canonicalDtoNames = new HashSet<string>(
            classified.Where(c => c.Tags.Contains("dto") || IsDataCarrier(c.Type)).Select(c => c.Type.Name),
            StringComparer.Ordinal);

        var byGroup = classified.GroupBy(c => c.Group, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        var shapes = new List<SectionShape>();
        var dtoAnchors = new List<DtoAnchor>();
        var interfaceAnchors = new List<InterfaceAnchor>();
        var shardAnchors = new List<ShardAnchor>();

        foreach (var rule in config.EffectiveSections)
        {
            var members = byGroup.TryGetValue(rule.Name, out var ms) ? ms : new List<ClassifiedType>();

            var repoIfaces = members.Where(c => c.Tags.Contains("repositoryInterface")).Select(c => c.Type.Name).ToList();
            var repoImpls = members.Where(c => c.Tags.Contains("repositoryImplementation")).Select(c => c.Type.Name).ToList();
            var readIfaceTypes = members.Where(c => c.Tags.Contains("readServiceInterface")).ToList();
            var fullIfaceTypes = members.Where(c => c.Tags.Contains("fullServiceInterface")).ToList();

            var facts = SectionFacts.For(rule, repoSectionNames);

            // Resolve primary / settings DTO names by config or convention.
            var primaryName = rule.PrimaryInfoDto ?? rule.Name + "Info";
            var settingsName = rule.SettingsInfoDto ?? rule.Name + "SettingsInfo";

            var primarySym = ResolveDtoSymbol(classified, primaryName, rule.Name);
            var settingsSym = ResolveDtoSymbol(classified, settingsName, rule.Name);

            DtoAnchor? primaryAnchor = primarySym is null ? null
                : new DtoAnchor(primarySym.ToDisplayString(), rule.Name, "primaryInfoDto",
                    DtoInventory.Build(primarySym, canonicalDtoNames));
            DtoAnchor? settingsAnchor = settingsSym is null ? null
                : new DtoAnchor(settingsSym.ToDisplayString(), rule.Name, "settingsInfoDto",
                    DtoInventory.Build(settingsSym, canonicalDtoNames));

            // Cache DTO: configured -> that; else (inference is Task 7) default to primary; else none.
            DtoAnchor? cacheAnchor = null;
            var cacheProvenance = "none";
            if (rule.CacheDto is not null)
            {
                var cacheSym = ResolveDtoSymbol(classified, rule.CacheDto, rule.Name);
                if (cacheSym is not null)
                {
                    cacheAnchor = new DtoAnchor(cacheSym.ToDisplayString(), rule.Name, "cacheDto",
                        DtoInventory.Build(cacheSym, canonicalDtoNames));
                    cacheProvenance = "configured";
                }
            }
            if (cacheAnchor is null && primaryAnchor is not null)
            {
                cacheAnchor = new DtoAnchor(primaryAnchor.Display, rule.Name, "cacheDto", primaryAnchor.Paths);
                cacheProvenance = "default-primary";
            }

            // Charged read methods (behavioral classification, not by name).
            var charged = new List<ChargedReadMethod>();
            foreach (var ri in readIfaceTypes)
            {
                foreach (var m in ri.Type.GetMembers().OfType<IMethodSymbol>())
                {
                    if (m.MethodKind != MethodKind.Ordinary || m.AssociatedSymbol is not null || m.IsImplicitlyDeclared) continue;
                    var kind = ReadSurface.Classify(m, primaryName, settingsName);
                    if (!ReadSurface.IsCharged(kind)) continue;
                    var (hatch, reason) = MatchEscapeHatch(rule, ri.Type.Name, m.Name);
                    var (file, line) = Locate(m, solutionDirectory);
                    charged.Add(new ChargedReadMethod(ri.Type.Name, m.Name, kind,
                        m.ReturnType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat), hatch, reason)
                    {
                        File = file,
                        Line = line,
                        Symbol = m
                    });
                }
            }

            // Missing surfaces (gated to repo-backed expectations via SectionFacts).
            var missing = new List<MissingSurface>();
            if (facts.RequiresReadSurface && readIfaceTypes.Count == 0)
                missing.Add(new MissingSurface(rule.Name, "missingReadSurface", $"{rule.Name}: no read-only service interface"));
            if (facts.RequiresWriteSurface && fullIfaceTypes.Count == 0)
                missing.Add(new MissingSurface(rule.Name, "missingWriteSurface", $"{rule.Name}: no write/full service interface"));
            if (facts.RequiresPrimaryInfoDto && primarySym is null)
                missing.Add(new MissingSurface(rule.Name, "missingPrimaryInfoDto", $"{rule.Name}: no DTO named {primaryName}"));

            var shards = rule.ReadShards.Select(s => new ShardAnchor(s.Name, s.Purpose, Array.Empty<string>())).ToList();

            shapes.Add(new SectionShape
            {
                Name = rule.Name,
                Facts = facts,
                OwnedRepositoryInterfaces = repoIfaces,
                OwnedRepositoryImplementations = repoImpls,
                FullServiceInterfaces = fullIfaceTypes.Select(c => c.Type.Name).ToList(),
                ReadServiceInterfaces = readIfaceTypes.Select(c => c.Type.Name).ToList(),
                PrimaryInfoDto = primaryAnchor,
                SettingsInfoDto = settingsAnchor,
                CacheDto = cacheAnchor,
                CacheDtoProvenance = cacheProvenance,
                ReadShards = shards,
                Missing = missing,
                Grandfathered = rule.GrandfatheredDependencies,
                EscapeHatches = rule.EscapeHatchReadMethods,
                ChargedReadMethods = charged
            });

            if (primaryAnchor is not null) dtoAnchors.Add(primaryAnchor);
            if (settingsAnchor is not null) dtoAnchors.Add(settingsAnchor);
            if (cacheAnchor is not null) dtoAnchors.Add(cacheAnchor);
            foreach (var ri in readIfaceTypes) interfaceAnchors.Add(BuildInterfaceAnchor(ri, rule.Name, "readServiceInterface"));
            foreach (var fi in fullIfaceTypes) interfaceAnchors.Add(BuildInterfaceAnchor(fi, rule.Name, "fullServiceInterface"));
            shardAnchors.AddRange(shards);
        }

        var dedupDtos = dtoAnchors
            .GroupBy(a => $"{a.Display}|{a.Role}", StringComparer.Ordinal)
            .Select(g => g.First())
            .ToList();

        return Task.FromResult(new SectionArchitecture(shapes, dedupDtos, interfaceAnchors, shardAnchors));
    }

    private static InterfaceAnchor BuildInterfaceAnchor(ClassifiedType iface, string section, string role)
    {
        var methods = iface.Type.GetMembers().OfType<IMethodSymbol>()
            .Where(m => m.MethodKind == MethodKind.Ordinary && m.AssociatedSymbol is null && !m.IsImplicitlyDeclared)
            .Select(m => new InterfaceAnchorMethod(m.Name,
                m.ReturnType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)))
            .ToList();
        return new InterfaceAnchor(iface.Type.ToDisplayString(), section, role, methods);
    }

    private static INamedTypeSymbol? ResolveDtoSymbol(List<ClassifiedType> classified, string name, string section)
    {
        var matches = classified.Where(c => c.Type.Name == name).ToList();
        if (matches.Count == 0) return null;
        var inSection = matches.FirstOrDefault(c => string.Equals(c.Group, section, StringComparison.OrdinalIgnoreCase));
        return (inSection ?? matches[0]).Type;
    }

    private static (bool Hatch, string? Reason) MatchEscapeHatch(SectionRule rule, string ifaceName, string methodName)
    {
        foreach (var e in rule.EscapeHatchReadMethods)
            if (GlobMatcher.MatchesName($"{ifaceName}.{methodName}", e.Method) || GlobMatcher.MatchesName(methodName, e.Method))
                return (true, e.Reason);
        return (false, null);
    }

    /// <summary>A pure data carrier: class/struct with public properties and no public methods.</summary>
    private static bool IsDataCarrier(INamedTypeSymbol t)
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

    private static (string File, int Line) Locate(ISymbol symbol, string solutionDirectory)
    {
        var loc = symbol.Locations.FirstOrDefault(l => l.IsInSource);
        if (loc is null) return ("", 0);
        var ls = loc.GetLineSpan();
        return (LocationHelper.NormalizePath(ls.Path, solutionDirectory), ls.StartLinePosition.Line + 1);
    }
}
