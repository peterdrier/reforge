using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

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
/// Resolves the architectural shape of each section from the shared <see cref="SolutionClassifier"/>
/// output. Section-architecture rules and the section-shape view both consume these shapes, so the
/// read/full pairing, DTO anchoring, and charged-read classification live in one place. Every
/// assembly-derived section is shaped; config contributes policy (DTO anchors, requirement
/// overrides, visible debt) where the structure alone can't say.
/// </summary>
public static class SectionShapeAnalyzer
{
    public static async Task<SectionArchitecture> AnalyzeAsync(Solution solution,
        List<ClassifiedType> classified, SurfaceScoreConfig config, string solutionDirectory, CancellationToken ct)
    {
        // Keyed by SolutionClassifier.TypeKey (declaring assembly + fully qualified name): the name
        // alone is not unique across a solution, and collapsing two assemblies' identically named
        // types would resolve a consumer into the wrong section.
        var typesByDisplay = classified.ToDictionary(
            c => SolutionClassifier.TypeKey(c.Type), c => c, StringComparer.Ordinal);

        // Cross-section write-surface use (with escape analysis), keyed by consumer section.
        var crossUses = await AnalyzeCrossSectionUsesAsync(solution, classified, typesByDisplay, config, solutionDirectory, ct);

        // Which sections own persistence? Derived, not configured: the assembly declares a
        // classified repository (interface or implementation) or a DbContext. Drives RepoBacked.
        var repoSectionNames = new HashSet<string>(
            classified.Where(c => c.Tags.Contains("repositoryInterface")
                               || c.Tags.Contains("repositoryImplementation")
                               || DbContextAnalyzer.IsDbContextType(c.Type))
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

        foreach (var name in byGroup.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            var rule = config.Policy(name);
            var members = byGroup[name];

            var repoIfaces = members.Where(c => c.Tags.Contains("repositoryInterface")).Select(c => c.Type.Name).ToList();
            var repoImpls = members.Where(c => c.Tags.Contains("repositoryImplementation")).Select(c => c.Type.Name).ToList();
            var readIfaceTypes = members.Where(c => c.Tags.Contains("readServiceInterface")).ToList();
            var fullIfaceTypes = members.Where(c => c.Tags.Contains("fullServiceInterface")).ToList();

            var facts = SectionFacts.For(name, rule, repoSectionNames);

            // Resolve primary / settings DTO: explicit config -> <Section>Info convention ->
            // canonicalReadDtos fallback. The fallback matters for real configs whose section
            // names are plural ("Camps") but whose DTOs are singular ("CampInfo") — the convention
            // would miss and spuriously fire missingPrimaryInfoDto.
            var (primaryName, primarySym) = ResolvePrimary(name, rule, classified);
            var (settingsName, settingsSym) = ResolveSettings(name, rule, classified);

            DtoAnchor? primaryAnchor = primarySym is null ? null
                : new DtoAnchor(primarySym.ToDisplayString(), name, "primaryInfoDto",
                    DtoInventory.Build(primarySym, canonicalDtoNames));
            DtoAnchor? settingsAnchor = settingsSym is null ? null
                : new DtoAnchor(settingsSym.ToDisplayString(), name, "settingsInfoDto",
                    DtoInventory.Build(settingsSym, canonicalDtoNames));

            // Cache DTO resolution: configured -> inferred from a caching decorator -> default to
            // primary -> none.
            DtoAnchor? cacheAnchor = null;
            var cacheProvenance = "none";
            if (rule.CacheDto is not null)
            {
                var cacheSym = ResolveDtoSymbol(classified, rule.CacheDto, name);
                if (cacheSym is not null)
                {
                    cacheAnchor = new DtoAnchor(cacheSym.ToDisplayString(), name, "cacheDto",
                        DtoInventory.Build(cacheSym, canonicalDtoNames));
                    cacheProvenance = "configured";
                }
            }
            if (cacheAnchor is null)
            {
                var inferred = InferCacheDto(readIfaceTypes, classified, canonicalDtoNames, name);
                if (inferred is not null)
                {
                    cacheAnchor = inferred.Value.Anchor;
                    cacheProvenance = inferred.Value.Provenance;
                }
            }
            if (cacheAnchor is null && primaryAnchor is not null)
            {
                cacheAnchor = new DtoAnchor(primaryAnchor.Display, name, "cacheDto", primaryAnchor.Paths);
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
                missing.Add(new MissingSurface(name, "missingReadSurface", $"{name}: no read-only service interface"));
            if (facts.RequiresWriteSurface && fullIfaceTypes.Count == 0)
                missing.Add(new MissingSurface(name, "missingWriteSurface", $"{name}: no write/full service interface"));
            if (facts.RequiresPrimaryInfoDto && primarySym is null)
                missing.Add(new MissingSurface(name, "missingPrimaryInfoDto", $"{name}: no DTO named {primaryName}"));

            // Advisory candidates (section-shape view): each charged read method is a derivability
            // candidate against its target DTO; facts not present on the target inventory are flagged;
            // and each charged read is a candidate to answer from the cache DTO.
            var derivable = new List<DerivableReadMethod>();
            var missingFacts = new List<MissingInfoFact>();
            var cacheFacts = new List<CacheFactCandidate>();
            var primaryPaths = primaryAnchor?.Paths ?? (IReadOnlyList<string>)Array.Empty<string>();
            var settingsPaths = settingsAnchor?.Paths ?? (IReadOnlyList<string>)Array.Empty<string>();
            foreach (var rm in charged)
            {
                var targetDto = rm.Kind == ReadMethodKind.ScalarFact ? settingsName : primaryName;
                var token = FactToken(rm.Method);
                derivable.Add(new DerivableReadMethod(rm.Interface, rm.Method, rm.Kind, targetDto,
                    $"answerable from {targetDto} (returns {rm.Returns})"));

                var paths = rm.Kind == ReadMethodKind.ScalarFact ? settingsPaths : primaryPaths;
                if (token.Length > 0 && !paths.Any(p => p.Contains(token, StringComparison.OrdinalIgnoreCase)))
                    missingFacts.Add(new MissingInfoFact($"{targetDto}.{token}", targetDto));

                if (cacheAnchor is not null)
                    cacheFacts.Add(new CacheFactCandidate(rm.Method, token, cacheAnchor.Display));
            }

            var shards = rule.ReadShards.Select(s => new ShardAnchor(s.Name, s.Purpose, Array.Empty<string>())).ToList();

            var (writeCallers, writeUnverified) = crossUses.TryGetValue(name, out var cu)
                ? cu
                : (new List<CrossSectionUse>(), new List<CrossSectionUse>());

            shapes.Add(new SectionShape
            {
                Name = name,
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
                WriteSurfaceCallers = writeCallers,
                WriteSurfaceUnverified = writeUnverified,
                Missing = missing,
                Grandfathered = rule.GrandfatheredDependencies,
                EscapeHatches = rule.EscapeHatchReadMethods,
                ChargedReadMethods = charged,
                DerivableReadMethods = derivable,
                MissingInfoFacts = missingFacts,
                CacheFactCandidates = cacheFacts
            });

            if (primaryAnchor is not null) dtoAnchors.Add(primaryAnchor);
            if (settingsAnchor is not null) dtoAnchors.Add(settingsAnchor);
            if (cacheAnchor is not null) dtoAnchors.Add(cacheAnchor);
            foreach (var ri in readIfaceTypes) interfaceAnchors.Add(BuildInterfaceAnchor(ri, name, "readServiceInterface"));
            foreach (var fi in fullIfaceTypes) interfaceAnchors.Add(BuildInterfaceAnchor(fi, name, "fullServiceInterface"));
            shardAnchors.AddRange(shards);
        }

        var dedupDtos = dtoAnchors
            .GroupBy(a => $"{a.Display}|{a.Role}", StringComparer.Ordinal)
            .Select(g => g.First())
            .ToList();

        return new SectionArchitecture(shapes, dedupDtos, interfaceAnchors, shardAnchors);
    }

    // ---------------- Cross-section write-surface use + escape analysis ----------------

    /// <summary>
    /// For each consumer class that injects ANOTHER section's full (write) interface paired with
    /// a read interface, classifies how the dependency is used:
    /// every observed call read-covered with no escape -> confident <c>crossSectionWriteSurface</c>
    /// candidate; the dependency escapes analysis (passed onward, returned, captured) -> an
    /// "unverified" advisory instead of a confident penalty.
    /// </summary>
    private static async Task<Dictionary<string, (List<CrossSectionUse> Confident, List<CrossSectionUse> Unverified)>>
        AnalyzeCrossSectionUsesAsync(Solution solution, List<ClassifiedType> classified,
            Dictionary<string, ClassifiedType> typesByDisplay, SurfaceScoreConfig config, string dir, CancellationToken ct)
    {
        var result = new Dictionary<string, (List<CrossSectionUse>, List<CrossSectionUse>)>(StringComparer.OrdinalIgnoreCase);
        var pairs = SurfaceScoreEngine.BuildFullToReadPairs(classified, typesByDisplay);
        if (pairs.Count == 0) return result;

        var readNamesByFull = pairs.ToDictionary(
            kv => kv.Key,
            kv => kv.Value.Type.GetMembers().OfType<IMethodSymbol>()
                    .Where(m => m.MethodKind == MethodKind.Ordinary)
                    .Select(m => m.Name).ToHashSet(StringComparer.Ordinal),
            StringComparer.Ordinal);

        foreach (var c in classified)
        {
            if (c.Type.TypeKind != TypeKind.Class) continue;
            var consumerRule = config.Policy(c.Group);

            foreach (var ctor in c.Type.Constructors)
            {
                if (ctor.IsImplicitlyDeclared) continue;
                foreach (var param in ctor.Parameters)
                {
                    var d = SolutionClassifier.TypeKey(param.Type);
                    if (!pairs.TryGetValue(d, out var readIface)) continue;
                    if (!typesByDisplay.TryGetValue(d, out var depFull)) continue;
                    if (string.Equals(depFull.Group, c.Group, StringComparison.OrdinalIgnoreCase)) continue; // same section

                    var (observed, escaped, file, line) = await AnalyzeDependencyUsageAsync(c, param, solution, dir, ct);

                    var use = new CrossSectionUse(c.Type.Name, c.Group, param.Type.Name, depFull.Group, readIface.Type.Name, observed)
                    {
                        File = file,
                        Line = line
                    };

                    if (!result.TryGetValue(c.Group, out var bucket))
                    {
                        bucket = (new List<CrossSectionUse>(), new List<CrossSectionUse>());
                        result[c.Group] = bucket;
                    }

                    if (escaped)
                    {
                        bucket.Item2.Add(use); // unverified advisory
                        continue;
                    }

                    var readNames = readNamesByFull[d];
                    bool allReadCovered = observed.Count > 0 && observed.All(readNames.Contains);
                    bool grandfathered = IsGrandfathered(consumerRule, c.Type.Name, param.Type.Name);
                    if (allReadCovered && !grandfathered)
                        bucket.Item1.Add(use); // confident penalty (also suppresses the generic rule)
                    // else: a full-only call, no call, or grandfathered -> the generic rule handles it.
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Walks a consumer's body and classifies every reference to the injected dependency (via its
    /// backing field or the ctor parameter): a method-call receiver is an observed call; anything
    /// else that consumes the reference as a value (argument, return, capture, reassignment) is an
    /// escape. The backing-field assignment itself is not an escape.
    /// </summary>
    private static async Task<(List<string> Observed, bool Escaped, string File, int Line)>
        AnalyzeDependencyUsageAsync(ClassifiedType c, IParameterSymbol param, Solution solution, string dir, CancellationToken ct)
    {
        var observed = new List<string>();
        var escaped = false;

        var ploc = param.Locations.FirstOrDefault(l => l.IsInSource) ?? c.PrimaryLocation;
        var pls = ploc.GetLineSpan();
        var file = LocationHelper.NormalizePath(pls.Path, dir);
        var line = pls.StartLinePosition.Line + 1;

        var backing = ResolveBackingField(c.Type, param);

        foreach (var declRef in c.Type.DeclaringSyntaxReferences)
        {
            var tree = declRef.SyntaxTree;
            var project = solution.Projects.FirstOrDefault(p => p.Documents.Any(doc => doc.FilePath == tree.FilePath));
            if (project is null) continue;
            var compilation = await project.GetCompilationAsync(ct);
            if (compilation is null) continue;
            var model = compilation.GetSemanticModel(tree);
            var classNode = await declRef.GetSyntaxAsync(ct);

            foreach (var id in classNode.DescendantNodes().OfType<IdentifierNameSyntax>())
            {
                var sym = model.GetSymbolInfo(id, ct).Symbol;
                if (sym is null) continue;
                bool isDep = (backing is not null && SymbolEqualityComparer.Default.Equals(sym, backing))
                          || SymbolEqualityComparer.Default.Equals(sym, param);
                if (!isDep) continue;

                // The dependency-denoting expression: `_camp`, or `this._camp` (member access).
                ExpressionSyntax e = id;
                if (id.Parent is MemberAccessExpressionSyntax pma && pma.Name == id)
                    e = pma;

                if (IsFieldWriteTarget(e)) continue;                       // writing TO the field (incl. backing init)
                if (IsBackingAssignmentSource(e, backing, model, ct)) continue; // ctor `_field = param`
                if (IsCallReceiver(e, out var calledName)) { observed.Add(calledName); continue; }
                escaped = true;
            }
        }

        return (observed, escaped, file, line);
    }

    private static IFieldSymbol? ResolveBackingField(INamedTypeSymbol type, IParameterSymbol param)
    {
        foreach (var ctor in type.Constructors)
        foreach (var declRef in ctor.DeclaringSyntaxReferences)
        foreach (var asn in declRef.GetSyntax().DescendantNodes().OfType<AssignmentExpressionSyntax>())
        {
            if (asn.Right is not IdentifierNameSyntax rid || rid.Identifier.Text != param.Name) continue;
            var fieldName = asn.Left switch
            {
                IdentifierNameSyntax lid => lid.Identifier.Text,
                MemberAccessExpressionSyntax lma => lma.Name.Identifier.Text,
                _ => null
            };
            if (fieldName is null) continue;
            var f = type.GetMembers(fieldName).OfType<IFieldSymbol>().FirstOrDefault();
            if (f is not null) return f;
        }
        return null;
    }

    private static bool IsFieldWriteTarget(ExpressionSyntax e)
        => e.Parent is AssignmentExpressionSyntax asn && asn.Left == e;

    private static bool IsBackingAssignmentSource(ExpressionSyntax e, IFieldSymbol? backing, SemanticModel model, CancellationToken ct)
    {
        if (backing is null) return false;
        if (e.Parent is not AssignmentExpressionSyntax asn || asn.Right != e) return false;
        return SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(asn.Left, ct).Symbol, backing);
    }

    private static bool IsCallReceiver(ExpressionSyntax e, out string calledName)
    {
        calledName = "";
        if (e.Parent is MemberAccessExpressionSyntax ma && ma.Expression == e
            && ma.Parent is InvocationExpressionSyntax inv && inv.Expression == ma)
        {
            calledName = ma.Name switch
            {
                GenericNameSyntax g => g.Identifier.Text,
                _ => ma.Name.Identifier.Text
            };
            return true;
        }
        return false;
    }

    private static bool IsGrandfathered(SectionRule rule, string caller, string fullInterface)
    {
        foreach (var g in rule.GrandfatheredDependencies)
            if (g.Dependency == $"{caller}->{fullInterface}" || g.Dependency == caller)
                return true;
        return false;
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

    /// <summary>
    /// Resolves the primary Info DTO: explicit <c>primaryInfoDto</c> -> <c>&lt;Section&gt;Info</c>
    /// convention -> first non-settings <c>canonicalReadDtos</c> entry that exists. Returns the
    /// chosen name (convention name when unresolved, for the missing-surface detail) and symbol.
    /// </summary>
    private static (string Name, INamedTypeSymbol? Symbol) ResolvePrimary(string section, SectionRule rule, List<ClassifiedType> classified)
    {
        if (rule.PrimaryInfoDto is not null)
            return (rule.PrimaryInfoDto, ResolveDtoSymbol(classified, rule.PrimaryInfoDto, section));

        var convention = section + "Info";
        var sym = ResolveDtoSymbol(classified, convention, section);
        if (sym is not null) return (convention, sym);

        foreach (var cn in rule.CanonicalReadDtos)
        {
            if (cn.EndsWith("SettingsInfo", StringComparison.Ordinal)) continue; // that's the settings DTO
            var cs = ResolveDtoSymbol(classified, cn, section);
            if (cs is not null) return (cn, cs);
        }
        return (convention, null);
    }

    /// <summary>Resolves the settings DTO: explicit -> <c>&lt;Section&gt;SettingsInfo</c> -> a <c>*SettingsInfo</c> canonical DTO.</summary>
    private static (string Name, INamedTypeSymbol? Symbol) ResolveSettings(string section, SectionRule rule, List<ClassifiedType> classified)
    {
        if (rule.SettingsInfoDto is not null)
            return (rule.SettingsInfoDto, ResolveDtoSymbol(classified, rule.SettingsInfoDto, section));

        var convention = section + "SettingsInfo";
        var sym = ResolveDtoSymbol(classified, convention, section);
        if (sym is not null) return (convention, sym);

        foreach (var cn in rule.CanonicalReadDtos)
        {
            if (!cn.EndsWith("SettingsInfo", StringComparison.Ordinal)) continue;
            var cs = ResolveDtoSymbol(classified, cn, section);
            if (cs is not null) return (cn, cs);
        }
        return (convention, null);
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

    /// <summary>
    /// Infers the cache DTO from a caching decorator: a class named <c>Cached*</c>/<c>*CachingDecorator</c>/
    /// <c>*Cache</c> that implements one of the section's read interfaces and holds a cache field whose
    /// value type is a source-declared DTO. Returns the decorator-provenance anchor, or null.
    /// </summary>
    private static (DtoAnchor Anchor, string Provenance)? InferCacheDto(
        List<ClassifiedType> readIfaceTypes, List<ClassifiedType> classified,
        IReadOnlySet<string> canonicalDtoNames, string section)
    {
        var readSymbols = readIfaceTypes.Select(c => c.Type).ToList();
        if (readSymbols.Count == 0) return null;

        foreach (var c in classified)
        {
            if (c.Type.TypeKind != TypeKind.Class) continue;
            if (!IsCachingDecoratorName(c.Type.Name)) continue;
            if (!c.Type.AllInterfaces.Any(i => readSymbols.Any(r => SymbolEqualityComparer.Default.Equals(i, r)))) continue;

            foreach (var f in c.Type.GetMembers().OfType<IFieldSymbol>())
            {
                if (CacheValueType(f.Type) is INamedTypeSymbol vt && vt.Locations.Any(l => l.IsInSource))
                {
                    var anchor = new DtoAnchor(vt.ToDisplayString(), section, "cacheDto", DtoInventory.Build(vt, canonicalDtoNames));
                    return (anchor, $"inferred:{c.Type.Name}");
                }
            }
        }
        return null;
    }

    /// <summary>A best-effort fact token from a read method name: strip a leading verb and trailing Async.</summary>
    private static string FactToken(string method)
    {
        var n = method;
        if (n.EndsWith("Async", StringComparison.Ordinal) && n.Length > 5) n = n[..^5];
        foreach (var pre in new[] { "Get", "Find", "Load", "Is", "Has" })
            if (n.StartsWith(pre, StringComparison.Ordinal) && n.Length > pre.Length) { n = n[pre.Length..]; break; }
        return n;
    }

    private static bool IsCachingDecoratorName(string name)
        => GlobMatcher.MatchesName(name, "Cached*")
        || GlobMatcher.MatchesName(name, "*CachingDecorator")
        || GlobMatcher.MatchesName(name, "*Cache");

    /// <summary>The value type carried by a cache field (Dictionary/ConcurrentDictionary value, or single generic arg).</summary>
    private static INamedTypeSymbol? CacheValueType(ITypeSymbol fieldType)
    {
        if (fieldType is not INamedTypeSymbol n || !n.IsGenericType) return null;
        var od = n.OriginalDefinition.ToDisplayString();
        bool collection = od.StartsWith("System.Collections.Generic.", StringComparison.Ordinal)
                       || od.StartsWith("System.Collections.Concurrent.", StringComparison.Ordinal);
        if (!collection) return null;
        if (n.TypeArguments.Length == 2) return n.TypeArguments[1] as INamedTypeSymbol; // keyed cache -> value
        if (n.TypeArguments.Length == 1) return n.TypeArguments[0] as INamedTypeSymbol; // set/list cache -> element
        return null;
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
