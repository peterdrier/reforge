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
    public List<GroupRule> Groups { get; set; } = new();
    public Dictionary<string, ClassificationRule> Classifications { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public ResourceConfig Resources { get; set; } = new();
    public Dictionary<string, int> Weights { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonIgnore] public bool GroupByNamespaceFallback { get; set; } = true;

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

        loaded.GroupByNamespaceFallback = loaded.Groups.Count == 0;
        loadedFrom = path;
        return loaded;
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

                // Dependency use
                ["sameSectionReadService"] = 0,
                ["crossSectionReadInterface"] = 2,
                ["crossSectionFullService"] = 8,
                ["crossSectionRepository"] = 25,

                // Internal shape
                ["methodParameterOverflow"] = 1, // per param after 2
                ["booleanParameter"] = 3,
                ["tupleReturn"] = 4,
                ["optionsBag"] = 8,
                ["dashboardAdminPageName"] = 6,
                ["oneImplementationInterface"] = 8
            }
        };
    }

    public int Weight(string key) => Weights.TryGetValue(key, out var v) ? v : 0;
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
            // Translate glob: ** -> .*, * -> [^/]*, escape the rest.
            var escaped = Regex.Escape(pattern)
                .Replace("/", "/")
                .Replace("\\*\\*", ".*")
                .Replace("\\*", "[^/]*");
            var rxStr = "^" + escaped + "$";
            rx = new Regex(rxStr, RegexOptions.Compiled | RegexOptions.IgnoreCase);
            PathCache[pattern] = rx;
        }
        return rx.IsMatch(path.Replace('\\', '/'));
    }
}
