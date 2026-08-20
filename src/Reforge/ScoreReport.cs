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
    string? Detail)
{
    /// <summary>
    /// Which assembly of the section declared the scored symbol: <c>contracts</c> for a satellite
    /// <c>&lt;Section&gt;.Contracts</c> assembly, <c>main</c> for everything else. Sections fold
    /// their contracts assembly in (see <see cref="AssemblySections"/>) so both land in one group;
    /// this keeps the origin the fold discards, because the two are not equally expensive.
    /// </summary>
    public string Origin { get; init; } = ScoreOrigin.Main;

    /// <summary>
    /// True when the contracts-assembly multiplier was applied to <see cref="Points"/>. Recorded
    /// so a reader who sees a 10-point DTO property can tell a doubled 5 from a weight change.
    /// </summary>
    public bool Multiplied { get; init; }
}

/// <summary>Origins a <see cref="ScoreEntry"/> can carry. Strings, not an enum, to keep the JSON stable.</summary>
public static class ScoreOrigin
{
    public const string Main = "main";
    public const string Contracts = "contracts";
}

public sealed class GroupScore
{
    public string Name { get; init; } = "";
    /// <summary>Combined total (surface + implementation shape). Informational; not an optimization target.</summary>
    public int Total { get; set; }
    /// <summary>Durable public-surface + dependency-use + return-shape points.</summary>
    public int SurfaceTotal { get; set; }
    /// <summary>Implementation-complexity points (cognitive complexity, size, dispatchers).</summary>
    public int ImplementationShapeTotal { get; set; }
    public Dictionary<string, int> ByRule { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<ScoreEntry> Entries { get; } = new();

    /// <summary>Surface points declared in the section's own assembly.</summary>
    public int MainSurfaceTotal { get; set; }

    /// <summary>
    /// Surface points declared in the section's satellite <c>&lt;Section&gt;.Contracts</c>
    /// assembly, after the multiplier. Reported beside <see cref="MainSurfaceTotal"/> rather than
    /// only summed into <see cref="SurfaceTotal"/>: a section can be small and publish a lot.
    /// </summary>
    public int ContractsSurfaceTotal { get; set; }

    /// <summary>
    /// Size and complexity of the section's corpus. Informational context for the score, never an
    /// input to it — see <see cref="SectionMetrics"/>. <see cref="SectionMetrics.Empty"/> when the
    /// report was built without a metrics pass (a hand-built report in a test, say).
    /// </summary>
    public SectionMetrics Metrics { get; set; } = SectionMetrics.Empty;

    /// <summary>
    /// Size of the section's test corpus — see <see cref="TestMass"/>. Informational, like
    /// <see cref="Metrics"/>: the test corpus is not scored at all.
    /// </summary>
    public TestMass Tests { get; set; } = TestMass.Empty;
}

public sealed class ScoreReport
{
    /// <summary>Combined total (surface + implementation shape). Informational; not an optimization target.</summary>
    public int Total { get; set; }
    public int SurfaceTotal { get; set; }
    public int ImplementationShapeTotal { get; set; }
    public Dictionary<string, GroupScore> Groups { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, int> ByRule { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> DuplicateOwners { get; } = new();
    public List<ScoreDiagnostic> Diagnostics { get; } = new();
    public string? ConfigPath { get; set; }
    public int TypesAnalyzed { get; set; }
    /// <summary>
    /// Solution-level size/complexity rollup, over the pooled sample rather than an average of the
    /// sections' averages. Informational — the score formula never reads it.
    /// </summary>
    public SectionMetrics Metrics { get; set; } = SectionMetrics.Empty;
    /// <summary>
    /// Metrics for every section in the corpus, including sections that scored nothing and so have
    /// no <see cref="GroupScore"/>. Keyed by section name.
    /// </summary>
    public Dictionary<string, SectionMetrics> MetricsBySection { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>
    /// Solution-level test-corpus rollup, including the mass of test projects no section claimed.
    /// </summary>
    public TestMass Tests { get; set; } = TestMass.Empty;
    /// <summary>
    /// Test mass per section, including sections that scored nothing. Keyed by section name.
    /// </summary>
    public Dictionary<string, TestMass> TestsBySection { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>
    /// Test projects whose non-test project references named no section, or named several with
    /// nothing to break the tie. Their mass is in <see cref="Tests"/> and in no section.
    /// </summary>
    public List<string> UnattributedTestProjects { get; } = new();
    /// <summary>
    /// Per section, the write-capable service interfaces its assembly exports. Reported, never
    /// scored — no weight reads it. Recorded where <c>fullServiceInterfaceMethod</c> is charged, so
    /// the population is the one the engine prices; per section rather than per interface because
    /// the per-interface distribution on the only corpus available is one outlier and a constant.
    /// </summary>
    public Dictionary<string, List<string>> PublicWriteSurface { get; } = new(StringComparer.OrdinalIgnoreCase);
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
    /// a group) where surface improved but implementation shape worsened past the threshold —
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
    int ShapeDelta,
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
