using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Reforge;

/// <summary>
/// Configuration for the <c>surface-score</c> command. Loaded from
/// <c>reforge.surface-score.json</c> next to the solution. Reforge is deliberately
/// domain-agnostic: every concept of "section", "ownership", "service kind" lives
/// in this config. If no file is present, <see cref="Default"/> supplies a generic
/// classification by name-pattern and groups by top-level namespace segment.
/// </summary>
public sealed class SurfaceScoreConfig
{
    /// <summary>
    /// Primary form. Keyed by section name. Each section can match by paths, namespaces,
    /// symbol-name globs, or explicit interface lists; the interface lists also auto-classify
    /// the named types (sugar — saves writing a separate classification entry).
    /// </summary>
    public Dictionary<string, SectionRule> Sections { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Legacy ordered form, kept for backward compatibility with 0.10/0.11 configs.
    /// Merged into <see cref="Sections"/> at load time.
    /// </summary>
    public List<GroupRule> Groups { get; set; } = new();
    public Dictionary<string, ClassificationRule> Classifications { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public ResourceConfig Resources { get; set; } = new();
    public Dictionary<string, int> Weights { get; set; } = new(StringComparer.OrdinalIgnoreCase);

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

    [JsonIgnore] public bool GroupByNamespaceFallback { get; set; } = true;

    /// <summary>
    /// Internal section list in declaration order. First match wins. Built once from
    /// <see cref="Sections"/> + <see cref="Groups"/> at load time so the engine has a
    /// single canonical view regardless of which form the user wrote.
    /// </summary>
    [JsonIgnore] public List<SectionRule> EffectiveSections { get; private set; } = new();

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

        // Merge defaults for any classification/weight the user didn't override.
        var defaults = Default();
        foreach (var (k, v) in defaults.Classifications)
            loaded.Classifications.TryAdd(k, v);
        foreach (var (k, v) in defaults.Weights)
            loaded.Weights.TryAdd(k, v);

        loaded.BuildEffectiveSections();
        loaded.GroupByNamespaceFallback = loaded.EffectiveSections.Count == 0;
        loadedFrom = path;
        return loaded;
    }

    /// <summary>
    /// Merges <see cref="Sections"/> + legacy <see cref="Groups"/> into the canonical
    /// <see cref="EffectiveSections"/> list. Dict ordering follows JSON insertion order
    /// (System.Text.Json preserves it for objects); legacy groups are appended after.
    /// </summary>
    public void BuildEffectiveSections()
    {
        EffectiveSections = new List<SectionRule>();
        foreach (var (name, rule) in Sections)
        {
            rule.Name = name;
            EffectiveSections.Add(rule);
        }
        foreach (var g in Groups)
        {
            EffectiveSections.Add(new SectionRule
            {
                Name = g.Name,
                Paths = g.Match.Paths,
                Namespaces = g.Match.Namespaces
            });
        }
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
        var config = new SurfaceScoreConfig
        {
            Groups = new List<GroupRule>(),
            GroupByNamespaceFallback = true,
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
                },
                // Used by methodReturnsEntityAcrossSection. A type is treated as an entity
                // when its declaration file lives under a Models or Entities folder, or its
                // namespace ends in .Models / .Domain.Entities — i.e. the project's domain layer.
                // Override or extend in config to match the actual layout.
                ["entity"] = new ClassificationRule
                {
                    NamePatterns = new() { "*Entity" },
                    Paths = new() { "**/Models/**", "**/Domain/Entities/**", "**/Entities/**" },
                    Namespaces = new() { }
                }
            },
            Resources = new ResourceConfig(),
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
                ["methodReturnsEntityAcrossSection"] = 15,

                // Dependency use
                ["sameSectionReadService"] = 0,
                ["crossSectionReadInterface"] = 2,
                ["crossSectionFullService"] = 8,
                ["crossSectionRepository"] = 25,
                ["writeCapableInterfaceUsedReadOnly"] = 12,

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
                ["flagsControlFlow"] = 1
            }
        };
        config.BuildEffectiveSections();
        return config;
    }

    public int Weight(string key) => Weights.TryGetValue(key, out var v) ? v : 0;

    /// <summary>
    /// Returns true if the named section is configured (in either <see cref="Sections"/>
    /// or legacy <see cref="Groups"/>). Useful for distinguishing "the user asked for a
    /// section that doesn't exist" from "the section exists but matched no types".
    /// </summary>
    public bool HasConfiguredSection(string name) =>
        EffectiveSections.Any(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Canonical section rule used by the engine. <see cref="SurfaceScoreConfig.Sections"/>
/// and legacy <see cref="SurfaceScoreConfig.Groups"/> are both merged into a list of
/// these at load time. A type joins a section if any one of its matchers fires.
/// </summary>
public sealed class SectionRule
{
    public string Name { get; set; } = "";
    public List<string> Paths { get; set; } = new();
    public List<string> Namespaces { get; set; } = new();
    public List<string> Symbols { get; set; } = new();
    /// <summary>Names that should be treated as repository interfaces (auto-classified and section-owned).</summary>
    public List<string> RepositoryInterfaces { get; set; } = new();
    /// <summary>Names that should be treated as full-service interfaces (auto-classified and section-owned).</summary>
    public List<string> ServiceInterfaces { get; set; } = new();
    /// <summary>Names that should be treated as read-only service interfaces (auto-classified and section-owned).</summary>
    public List<string> ReadServiceInterfaces { get; set; } = new();
    /// <summary>
    /// Canonical read DTOs for this section. When any public method's return type's simple
    /// name matches one of these (across any section), that method earns the
    /// <c>canonicalReadDtoReturn</c> credit. Canonical DTOs are also exempt from the
    /// <c>methodReturnsEntityAcrossSection</c> penalty even if their simple name would
    /// otherwise match the entity classification.
    /// </summary>
    public List<string> CanonicalReadDtos { get; set; } = new();
}

public sealed class GroupRule
{
    public string Name { get; set; } = "";
    public MatchSpec Match { get; set; } = new();
}

public sealed class MatchSpec
{
    public List<string> Paths { get; set; } = new();
    public List<string> Namespaces { get; set; } = new();
}

public sealed class ClassificationRule
{
    public List<string> NamePatterns { get; set; } = new();
    public List<string> Paths { get; set; } = new();
    public List<string> Namespaces { get; set; } = new();
    public List<string> Inherits { get; set; } = new();
    public List<string> AttributeNames { get; set; } = new();
}

public sealed class ResourceConfig
{
    public DbSetConfig DbSets { get; set; } = new();
}

public sealed class DbSetConfig
{
    public Dictionary<string, string> OwnerByName { get; set; } = new(StringComparer.OrdinalIgnoreCase);
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
