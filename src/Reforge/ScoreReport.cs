// The shapes the engine produces. Split out of SurfaceScoreEngine.cs so the engine files
// hold rules and this one holds the contract every consumer (JSON, Markdown, baseline
// comparison) reads.

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
    /// <summary>Combined total (surface + internal complexity). Informational; not an optimization target.</summary>
    public int Total { get; set; }
    /// <summary>Durable public-surface + dependency-use + return-shape points.</summary>
    public int SurfaceTotal { get; set; }
    /// <summary>Implementation-complexity points (cognitive complexity, size, dispatchers).</summary>
    public int InternalComplexityTotal { get; set; }
    public Dictionary<string, int> ByRule { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<ScoreEntry> Entries { get; } = new();
}

public sealed class ScoreReport
{
    /// <summary>Combined total (surface + internal complexity). Informational; not an optimization target.</summary>
    public int Total { get; set; }
    public int SurfaceTotal { get; set; }
    public int InternalComplexityTotal { get; set; }
    public Dictionary<string, GroupScore> Groups { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, int> ByRule { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> DuplicateOwners { get; } = new();
    public List<ScoreDiagnostic> Diagnostics { get; } = new();
    public string? ConfigPath { get; set; }
    public int TypesAnalyzed { get; set; }
    /// <summary>
    /// Compilation health of the analyzed solution. Defaults to a non-degraded value
    /// so the JSON `build` object is always present. Populated by ScoreAsync.
    /// </summary>
    public BuildHealth BuildHealth { get; set; } = new(false, 0, 0, false);
    /// <summary>
    /// The solution's section names (one per analyzed assembly, <c>.Contracts</c> folded in) —
    /// used by --list-groups and the missing-section diagnostic. Serialized as
    /// <c>configuredSections</c> for downstream consumers.
    /// </summary>
    public List<string> ConfiguredSections { get; } = new();
    /// <summary>
    /// Populated only when a <c>--baseline</c> is supplied. Each entry is a scope (solution or
    /// a group) where surface improved but internal complexity worsened past the threshold —
    /// the score-driven-consolidation smell. Empty otherwise.
    /// </summary>
    public List<SuspiciousImprovement> SuspiciousImprovements { get; } = new();
    /// <summary>
    /// Per-section conservation anchors (canonical DTOs + read/full interfaces) the Plan C gate
    /// holds refactors to. Always emitted (report-level, independent of any top-symbols cap).
    /// </summary>
    public List<ConservationAnchor> ConservationAnchors { get; set; } = new();
    /// <summary>
    /// Stateless sink classes (static/extension/fieldless) and their public methods — the baseline
    /// conservation gate diffs these against the baseline to detect a NEW helper absorbing a
    /// removed read/service method (helper-extraction gaming).
    /// </summary>
    public List<HelperCandidate> HelperCandidates { get; set; } = new();
}

public sealed record ScoreDiagnostic(string Level, string Code, string Message);

/// <summary>
/// A scope where a baseline comparison shows surface dropping while complexity rises past
/// the gate — i.e. the change looks like progress on the surface axis but is not a Pareto
/// improvement. <see cref="Improvement"/> is the authoritative verdict the loop should read.
/// </summary>
public sealed record SuspiciousImprovement(
    string Scope,
    string Kind,
    string Message,
    int SurfaceDelta,
    int InternalDelta,
    bool Improvement);

public sealed record ConservationAnchorMethod(string Name, string Returns);

/// <summary>
/// A stateless sink (static class, extension holder, or fieldless non-interface-backed class) and
/// its public method names — a candidate destination for helper-extraction gaming that the
/// baseline conservation gate watches for (a removed read method reappearing on a new helper).
/// </summary>
public sealed record HelperCandidate(string Display, IReadOnlyList<string> Methods);

/// <summary>
/// A fully-qualified, section-keyed anchor the conservation gate can hold a refactor to: a
/// canonical DTO (with its recursive member paths) or a read/full service interface (with its
/// method signatures). <see cref="ByRule"/> carries the surface points attributed to the anchor.
/// </summary>
public sealed record ConservationAnchor(
    string Key,
    string Section,
    string Role,
    IReadOnlyList<string> Paths,
    IReadOnlyList<ConservationAnchorMethod> Methods,
    Dictionary<string, int> ByRule);
