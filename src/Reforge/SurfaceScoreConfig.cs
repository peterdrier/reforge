using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Reforge;

/// <summary>
/// Configuration for the <c>surface-score</c> command. Loaded from
/// <c>reforge.surface-score.json</c> next to the solution. Reforge is deliberately
/// domain-agnostic: every concept of "ownership" and "service kind" lives in this config.
/// Sections are NOT configured — they are the solution's assemblies (see
/// <see cref="AssemblySections"/>). If no file is present, <see cref="Default"/> supplies a
/// generic classification by name-pattern and the standard weights.
/// </summary>
public sealed class SurfaceScoreConfig
{
    /// <summary>
    /// Per-section <b>policy</b>, keyed by the assembly-derived section name (e.g. "Store" for
    /// <c>Humans.Store</c>). Purely optional: a section with no entry gets <see cref="SectionRule.None"/>.
    /// This map no longer decides membership — it only carries the DTO anchors, surface-requirement
    /// overrides, and visible-debt lists that cannot be derived from structure.
    /// </summary>
    public Dictionary<string, SectionRule> Sections { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, ClassificationRule> Classifications { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, int> Weights { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The classification keys that came from the config <i>file</i>, recorded before the defaults
    /// are merged in. Not serialized — it describes where a value came from, not what it is.
    /// </summary>
    /// <remarks>
    /// Needed because the merge is <c>TryAdd</c>: a user block <b>replaces</b> the default entry for
    /// that key rather than extending it, so a block whose globs match nothing silently switches its
    /// rules off — the defaults that would have matched are gone. Distinguishing declared from
    /// defaulted is what lets <c>surface-score</c> warn about the former without warning about the
    /// latter on every solution that ships no config at all.
    /// </remarks>
    [JsonIgnore]
    public HashSet<string> DeclaredClassifications { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The classification tags the scoring rules actually read. This is exactly the default key set:
    /// a tag is only ever consumed by name, and every consumer's name is one of these, so a declared
    /// key outside this set is inert no matter what it matches.
    /// </summary>
    /// <remarks>
    /// Typed as <see cref="IReadOnlySet{T}"/>, not <c>IReadOnlyCollection</c>: the set carries an
    /// OrdinalIgnoreCase comparer, and only the set interface routes <c>Contains</c> through it.
    /// A collection-typed member would bind to the LINQ extension and compare case-sensitively.
    /// </remarks>
    public static IReadOnlySet<string> KnownClassifications { get; } =
        Default().Classifications.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Multiplies every surface charge on a declaration in a satellite
    /// <c>&lt;Section&gt;.Contracts</c> assembly. Default 2. Set to 1 to price both contracts
    /// shapes the same.
    /// </summary>
    /// <remarks>
    /// The same type under a <c>Contracts/</c> folder inside the section's own assembly is charged
    /// once. The difference is reach: reaching the folder means referencing the whole assembly,
    /// while a satellite assembly can be referenced on its own, by anyone, without taking a
    /// dependency on the implementation. That is the point of the shape and also what makes it the
    /// most expensive surface to withdraw. Credits and the internal-complexity axis are never
    /// scaled — see <c>SurfaceScoreEngine.ApplyContractsMultiplier</c>.
    /// </remarks>
    public int ContractsAssemblyMultiplier { get; set; } = 2;

    /// <summary>
    /// Glob patterns matched against a dispatcher parameter's <i>type</i> name. These act
    /// only as a tie-breaker — the read/write decision is made behaviorally (does the
    /// method mutate?), so a shape type cannot be used to evade a penalty on a mutation by
    /// renaming. Listing a type here additionally exempts borderline read methods whose
    /// behavior is ambiguous. Mutations are never exempted regardless of this list.
    /// </summary>
    public List<string> AllowedShapeTypes { get; set; } = new();

    /// <summary>
    /// Glob patterns matched against <c>Type.Method</c> (e.g. <c>RotaService.GetRotaAsync</c>).
    /// A matching method is fully exempt from the structural dispatcher / flags penalties —
    /// the escape hatch for a genuinely cohesive dispatcher the heuristic misjudges.
    /// </summary>
    public List<string> AllowedDispatcherMethods { get; set; } = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public const string DefaultFileName = "reforge.surface-score.json";

    /// <summary>
    /// Loads config from an explicit path, an upward search for
    /// <see cref="DefaultFileName"/>, or returns <see cref="Default"/>. Missing file
    /// is never an error — the generic defaults are designed to produce a useful
    /// (if shallow) score for any C# solution.
    /// </summary>
    public static SurfaceScoreConfig LoadOrDefault(string? explicitPath, string solutionDirectory, out string? loadedFrom)
    {
        loadedFrom = null;
        var path = explicitPath ?? DiscoverConfigFile(solutionDirectory);
        if (path is null || !File.Exists(path))
            return Default();

        var json = File.ReadAllText(path);
        var loaded = JsonSerializer.Deserialize<SurfaceScoreConfig>(json, JsonOptions)
                     ?? throw new InvalidOperationException($"Config at {path} parsed to null");

        // System.Text.Json assigns BRAND-NEW dictionaries through these setters, discarding the
        // OrdinalIgnoreCase comparers from the field initializers — so a config key differing only
        // in case from the name it targets would silently never match. That is easy to hit now that
        // section names come from assembly names (a hand-written "camp" key vs the derived "Camp")
        // rather than being defined by these keys themselves. Rebuild before the defaults merge, so
        // TryAdd below also dedupes case-variants instead of keeping both.
        loaded.Sections = CaseInsensitive(loaded.Sections);
        loaded.Classifications = CaseInsensitive(loaded.Classifications);
        loaded.Weights = CaseInsensitive(loaded.Weights);

        // Record authorship before the merge — afterwards the two are indistinguishable.
        loaded.DeclaredClassifications = new HashSet<string>(loaded.Classifications.Keys, StringComparer.OrdinalIgnoreCase);

        // Merge defaults for any classification/weight the user didn't override. TryAdd, so a
        // declared key wins outright: the default patterns for that key are NOT also applied.
        var defaults = Default();
        foreach (var (k, v) in defaults.Classifications)
            loaded.Classifications.TryAdd(k, v);
        foreach (var (k, v) in defaults.Weights)
            loaded.Weights.TryAdd(k, v);

        loadedFrom = path;
        return loaded;
    }

    /// <summary>
    /// Re-keys a deserialized dictionary case-insensitively. A source that contains two keys
    /// differing only in case keeps the last one rather than throwing — config is user input,
    /// and a duplicate key is not worth failing the whole run over.
    /// </summary>
    private static Dictionary<string, T> CaseInsensitive<T>(Dictionary<string, T> source)
    {
        var result = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in source)
            result[key] = value;
        return result;
    }

    private static string? DiscoverConfigFile(string solutionDirectory)
    {
        var dir = new DirectoryInfo(solutionDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, DefaultFileName);
            if (File.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    /// <summary>
    /// Generic defaults. No domain-specific knowledge — only conventional name patterns.
    /// Calling code with no config file will still get meaningful output.
    /// </summary>
    public static SurfaceScoreConfig Default()
    {
        return new SurfaceScoreConfig
        {
            Classifications = new(StringComparer.OrdinalIgnoreCase)
            {
                ["dto"] = new ClassificationRule
                {
                    NamePatterns = new() { "*Dto", "*DTO", "*Info", "*Command", "*Result", "*Request", "*Response", "*Model", "*View" }
                },
                ["readServiceInterface"] = new ClassificationRule
                {
                    NamePatterns = new() { "I*ServiceRead", "I*ReadService", "I*QueryService" }
                },
                ["fullServiceInterface"] = new ClassificationRule
                {
                    NamePatterns = new() { "I*Service" }
                },
                ["repositoryInterface"] = new ClassificationRule
                {
                    NamePatterns = new() { "I*Repository" }
                },
                ["repositoryImplementation"] = new ClassificationRule
                {
                    NamePatterns = new() { "*Repository" }
                },
                ["applicationService"] = new ClassificationRule
                {
                    NamePatterns = new() { "*Service" }
                },
                ["controller"] = new ClassificationRule
                {
                    NamePatterns = new() { "*Controller" },
                    Inherits = new() { "ControllerBase", "Controller" }
                },
                ["backgroundJob"] = new ClassificationRule
                {
                    NamePatterns = new() { "*Job", "*Worker", "*BackgroundService" },
                    Inherits = new() { "BackgroundService", "IHostedService" }
                }
                // There is no `entity` classification. It existed for a single rule,
                // methodReturnsEntityAcrossSection, and both were retired together — see the
                // changelog. A config still declaring one is reported as
                // `unknown-config-classification` rather than silently doing nothing.
            },
            Weights = new(StringComparer.OrdinalIgnoreCase)
            {
                // Durable surface
                ["dtoScalarProperty"] = 1,
                ["dtoCollectionProperty"] = 2,
                ["dtoNestedProperty"] = 3,
                ["publicDtoType"] = 5,
                ["applicationServiceMethod"] = 5,
                ["readServiceInterfaceMethod"] = 6,
                ["fullServiceInterfaceMethod"] = 8,
                ["repositoryInterfaceMethod"] = 10,
                ["repositoryImplementationMethod"] = 10,
                ["newRepositoryInterface"] = 15,
                ["newRepositoryImplementation"] = 15,
                ["diRegistration"] = 3,
                ["controllerAction"] = 8,
                ["backgroundJob"] = 12,
                ["duplicateDbSetOwner"] = 20,
                ["canonicalReadDtoReturn"] = -3,

                // Boundary-input surface — a parameter-object refactor can dodge
                // methodParameterOverflow by moving a long argument list into an input/command
                // object. These charge for that object as durable boundary surface (multipliers
                // over base points computed from the symbol model; default 1, 0 disables).
                ["publicInputWithHiddenState"] = 1,
                ["parameterBagInput"] = 1,
                ["inlineParameterObjectConstruction"] = 1,

                // Dependency use
                ["sameSectionReadService"] = 0,
                ["crossSectionReadInterface"] = 2,
                ["crossSectionFullService"] = 8,
                ["crossSectionRepository"] = 25,
                ["writeCapableInterfaceUsedReadOnly"] = 12,

                // Section architecture (surface axis)
                ["crossSectionWriteSurface"] = 15,
                ["missingReadSurface"] = 10,
                ["missingWriteSurface"] = 10,
                ["missingPrimaryInfoDto"] = 10,
                ["readSurfaceProjectionMethod"] = 4,

                // Internal shape (surface axis — method/return shape smells)
                ["methodParameterOverflow"] = 1, // per param after 2
                ["booleanParameter"] = 3,
                ["tupleReturn"] = 4,
                ["optionsBag"] = 8,
                ["dashboardAdminPageName"] = 6,
                ["oneImplementationInterface"] = 8,

                // Internal complexity axis — implementation cost hiding behind the surface.
                // These weights are MULTIPLIERS over base points computed from syntax
                // (cognitive complexity, LOC tiers, dispatcher arm count); default 1 applies
                // the base points as-is, 0 disables the rule. They are tracked on a separate
                // scalar (internalComplexityTotal) and are never added into the surface score.
                ["longMethod"] = 1,
                ["largeClass"] = 1,
                ["cognitiveComplexity"] = 1,
                ["actionDispatcher"] = 1,
                ["genericActionDispatcher"] = 1,
                ["mutationModeParameter"] = 1,
                ["flagsControlFlow"] = 1
            }
        };
    }

    public int Weight(string key) => Weights.TryGetValue(key, out var v) ? v : 0;

    /// <summary>
    /// The policy block for an assembly-derived section, or <see cref="SectionRule.None"/> when
    /// the section has no config entry (the normal case — policy is the exception, not the rule).
    /// </summary>
    public SectionRule Policy(string section) =>
        Sections.TryGetValue(section, out var rule) ? rule : SectionRule.None;

    /// <summary>
    /// The warning for section blocks that still declare the removed <c>canonicalReadDtos</c> list,
    /// or null when none do. Every command that resolves DTO anchors reports it — the field used to
    /// change both the return-type score and the primary/settings anchors, so silence would let a
    /// stale config look like it is still doing its job.
    /// </summary>
    public string? RemovedCanonicalReadDtosWarning()
    {
        var stale = Sections
            .Where(s => s.Value.DeclaresRemovedCanonicalReadDtos)
            .Select(s => s.Key)
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();
        if (stale.Count == 0) return null;

        return $"'canonicalReadDtos' is no longer read and is ignored in: {string.Join(", ", stale)}. " +
               "Canonical read DTOs are derived from each section's exported contracts surface " +
               "(a <Section>.Contracts assembly, or a Contracts/ folder in the section's assembly).";
    }
}

/// <summary>
/// Per-section policy: the facts about a section that structure cannot state. Section
/// <i>membership</i> is not here — that is the containing assembly (see <see cref="AssemblySections"/>).
/// </summary>
public sealed class SectionRule
{
    /// <summary>Shared empty policy for sections with no config entry. Never mutated by the engine.</summary>
    public static readonly SectionRule None = new();

    /// <summary>
    /// Config members this version no longer reads, captured so a stale block can be reported
    /// instead of silently changing meaning. <c>canonicalReadDtos</c> lived here until it became
    /// derived (see <see cref="CanonicalReadDtoSet"/>).
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unrecognized { get; set; }

    /// <summary>Primary read/cache DTO for this section. Default convention: "&lt;Section&gt;Info".</summary>
    public string? PrimaryInfoDto { get; set; }
    /// <summary>Settings DTO for this section. Default convention: "&lt;Section&gt;SettingsInfo".</summary>
    public string? SettingsInfoDto { get; set; }
    /// <summary>Cache value DTO. Default: == PrimaryInfoDto; else inferred from a caching decorator.</summary>
    public string? CacheDto { get; set; }
    /// <summary>Documented read shards (narrow read models intentionally split off the primary read surface).</summary>
    public List<ReadShard> ReadShards { get; set; } = new();
    /// <summary>Override: does this section require a read surface? Null = inferred from repo-backed.</summary>
    public bool? RequiresReadSurface { get; set; }
    /// <summary>Override: does this section require a write/full surface? Null = inferred from repo-backed.</summary>
    public bool? RequiresWriteSurface { get; set; }
    /// <summary>Override: does this section require a primary Info DTO? Null = inferred from repo-backed.</summary>
    public bool? RequiresPrimaryInfoDto { get; set; }
    /// <summary>
    /// Whether this block still carries the removed <c>canonicalReadDtos</c> list. The set is now
    /// derived from the section's exported contracts surface, so the list is inert — and inert
    /// config that used to grant credit and suppress a penalty must be reported, not ignored.
    /// </summary>
    public bool DeclaresRemovedCanonicalReadDtos =>
        Unrecognized is not null
        && Unrecognized.Keys.Any(k => k.Equals("canonicalReadDtos", StringComparison.OrdinalIgnoreCase));

    /// <summary>Cross-section write/full dependencies exempt from crossSectionWriteSurface (visible debt).</summary>
    public List<GrandfatheredDependency> GrandfatheredDependencies { get; set; } = new();
    /// <summary>Read methods exempt from readSurfaceProjectionMethod (visible debt).</summary>
    public List<EscapeHatchReadMethod> EscapeHatchReadMethods { get; set; } = new();
}

public sealed class ClassificationRule
{
    public List<string> NamePatterns { get; set; } = new();
    public List<string> Paths { get; set; } = new();
    public List<string> Namespaces { get; set; } = new();
    public List<string> Inherits { get; set; } = new();
    public List<string> AttributeNames { get; set; } = new();
}

public sealed class ReadShard
{
    public string Name { get; set; } = "";
    public string Purpose { get; set; } = "";
}

public sealed class GrandfatheredDependency
{
    /// <summary>"CallerType" or "CallerType-&gt;ICalleeInterface".</summary>
    public string Dependency { get; set; } = "";
    public string Reason { get; set; } = "";
    public string Since { get; set; } = "";
    public string? Owner { get; set; }
}

public sealed class EscapeHatchReadMethod
{
    /// <summary>Glob over "Interface.Method" or "Method".</summary>
    public string Method { get; set; } = "";
    public string Reason { get; set; } = "";
    public string Since { get; set; } = "";
    public string? Owner { get; set; }
}

/// <summary>
/// Compiled matchers — glob-to-regex once, then reuse. The regex cache lives here
/// so the engine doesn't recompile patterns per-symbol.
/// </summary>
public static class GlobMatcher
{
    private static readonly Dictionary<string, Regex> NameCache = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, Regex> PathCache = new(StringComparer.Ordinal);

    public static bool MatchesName(string name, string pattern)
    {
        if (!NameCache.TryGetValue(pattern, out var rx))
        {
            var rxStr = "^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$";
            rx = new Regex(rxStr, RegexOptions.Compiled);
            NameCache[pattern] = rx;
        }
        return rx.IsMatch(name);
    }

    public static bool MatchesPath(string path, string pattern)
    {
        if (string.IsNullOrEmpty(path)) return false;
        if (!PathCache.TryGetValue(pattern, out var rx))
        {
            // Hand-rolled glob-to-regex so we can give `**/` the standard meaning
            // "zero or more path segments". A naive escape-then-replace forces at least
            // one segment before `**/foo`, which is the opposite of what users expect.
            var sb = new System.Text.StringBuilder();
            sb.Append('^');
            int i = 0;
            while (i < pattern.Length)
            {
                char c = pattern[i];
                if (c == '*' && i + 1 < pattern.Length && pattern[i + 1] == '*')
                {
                    if (i + 2 < pattern.Length && pattern[i + 2] == '/')
                    {
                        sb.Append("(?:.*/)?"); // **/ -> zero or more path segments (each ending with /)
                        i += 3;
                    }
                    else
                    {
                        sb.Append(".*"); // bare ** -> any characters including /
                        i += 2;
                    }
                }
                else if (c == '*')
                {
                    sb.Append("[^/]*");
                    i++;
                }
                else if (c == '?')
                {
                    sb.Append("[^/]");
                    i++;
                }
                else
                {
                    sb.Append(Regex.Escape(c.ToString()));
                    i++;
                }
            }
            sb.Append('$');
            rx = new Regex(sb.ToString(), RegexOptions.Compiled | RegexOptions.IgnoreCase);
            PathCache[pattern] = rx;
        }
        return rx.IsMatch(path.Replace('\\', '/'));
    }
}
