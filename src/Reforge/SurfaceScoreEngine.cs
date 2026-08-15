using Microsoft.CodeAnalysis;

namespace Reforge;

/// <summary>
/// Computes a Surface Score for a Roslyn <see cref="Solution"/> using the supplied
/// <see cref="SurfaceScoreConfig"/>. The engine is intentionally generic: sections come from
/// the solution's assemblies, and the remaining domain knowledge (classifications, weights,
/// per-section policy) enters only through config.
/// <para>
/// This file holds the order the passes run in and the accumulator they all write through. Each
/// pass lives in its own <c>SurfaceScoreEngine.*.cs</c> partial, named for what it charges for —
/// so a rule change touches one file, and <see cref="ScoreAsync"/> stays readable as the sequence
/// it is. The report shapes are in <c>ScoreReport.cs</c>.
/// </para>
/// </summary>
public sealed partial class SurfaceScoreEngine
{
    private readonly SurfaceScoreConfig _config;
    private readonly string _solutionDirectory;

    public SurfaceScoreEngine(SurfaceScoreConfig config, string solutionDirectory)
    {
        _config = config;
        _solutionDirectory = solutionDirectory;
    }

    public async Task<ScoreReport> ScoreAsync(Solution solution, CancellationToken ct, int maxBuildDiagnostics = 25)
    {
        var report = new ScoreReport();
        var classified = (await SolutionClassifier.ClassifyAsync(solution, _config, _solutionDirectory, ct)).ToList();
        report.TypesAnalyzed = classified.Count;
        report.ConfiguredSections.AddRange(classified
            .Select(c => c.Group).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.Ordinal));

        // Policy keyed to a section that no assembly produces is inert by design — but silently
        // inert is a trap when config keys used to DEFINE the sections and now have to match
        // assembly-derived names. Name them so a mis-keyed or stale block is visible instead of
        // quietly dropping its DTO anchors, overrides, and grandfathered debt.
        var unknownSections = _config.Sections.Keys
            .Where(k => !report.ConfiguredSections.Contains(k, StringComparer.OrdinalIgnoreCase))
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();
        if (unknownSections.Count > 0)
            report.Diagnostics.Add(new ScoreDiagnostic("warning", "unknown-config-section",
                $"Config section policy has no matching assembly and is ignored: {string.Join(", ", unknownSections)}. " +
                $"Sections are assembly-derived; known sections are: {string.Join(", ", report.ConfiguredSections)}."));

        // canonicalReadDtos is derived from each section's exported contracts surface now. A config
        // still carrying the list would otherwise change meaning in silence — it used to grant the
        // canonicalReadDtoReturn credit and suppress methodReturnsEntityAcrossSection solution-wide.
        var removedField = _config.RemovedCanonicalReadDtosWarning();
        if (removedField is not null)
            report.Diagnostics.Add(new ScoreDiagnostic("warning", "removed-config-field", removedField));

        // Single canonical index — built once, used by both dependency-use and DI-registration
        // passes. Keyed by SolutionClassifier.TypeKey (declaring assembly + fully qualified name):
        // the name alone is not unique across a solution, and collapsing two assemblies' identically
        // named types would resolve a consumer into the wrong section.
        var typesByDisplay = classified.ToDictionary(
            c => SolutionClassifier.TypeKey(c.Type), c => c, StringComparer.Ordinal);

        // Section architecture (Plan B): resolve each configured section's shape once. Used to
        // score the five section rules and to emit conservation anchors. Computed before Pass 5
        // so the cross-section specialization can suppress the generic rule for the same pairs.
        var architecture = await SectionShapeAnalyzer.AnalyzeAsync(solution, classified, _config, _solutionDirectory, ct);

        // Confident cross-section read-only pairs (caller, full-interface simple name) — the generic
        // writeCapableInterfaceUsedReadOnly rule is suppressed for these in favor of the
        // section-specialized crossSectionWriteSurface penalty.
        var crossSectionSuppress = new HashSet<(string Caller, string Dependency)>();
        foreach (var s in architecture.Sections)
            foreach (var use in s.WriteSurfaceCallers)
                crossSectionSuppress.Add((use.Caller, use.Dependency));

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
        await ScoreWriteCapableUsedReadOnlyAsync(classified, typesByDisplay, solution, report, crossSectionSuppress, ct);

        // Cross-cutting: duplicate DbSet owners (resource ownership), DI registrations,
        // one-implementation interfaces.
        ScoreDuplicateDbSetOwners(classified, solution, report, ct);
        await ScoreDiRegistrationsAsync(solution, typesByDisplay, report, ct);
        ScoreOneImplementationInterfaces(classified, report);

        // Pass 6 — internal complexity (separate scalar). Cognitive complexity, method/class
        // size, and structural action-dispatcher detection — the counterweight to surface.
        ScoreImplementationComplexity(classified, report, ct);

        // Pass 7 — boundary-input surface. Charges for parameter/command objects that hide a
        // long argument list (so a parameter-object refactor can't game methodParameterOverflow).
        ScoreBoundaryInputs(classified, typesByDisplay, report, ct);
        await ScoreInlineParameterObjectConstructionAsync(typesByDisplay, solution, report, ct);

        // Section-architecture scored rules (surface axis) + conservation anchors.
        ScoreSectionArchitecture(architecture, report);
        report.ConservationAnchors = BuildConservationAnchors(architecture, report);
        report.HelperCandidates = BuildHelperCandidates(classified);

        // Build health: detect a degraded (unbuilt/erroring) compilation so a partial
        // score is never mistaken for a complete one. Reuses the per-project compilations
        // the passes above already realized (Roslyn caches them), so this is near-free.
        report.BuildHealth = await BuildInspector.InspectAsync(solution, maxBuildDiagnostics, ct);

        return report;
    }

    // ---------------- Shared accumulator ----------------

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
        => AddEntryByName(report, groupName, rule, points, symbol.Name, file, line, detail);

    /// <summary>
    /// Section-level entries (missing surfaces, cross-section uses) aren't tied to a single
    /// declared symbol, so they carry an explicit name instead of an <see cref="ISymbol"/>.
    /// </summary>
    private static void AddEntryByName(ScoreReport report, string groupName, string rule, int points,
        string symbolName, string file, int line, string? detail)
    {
        if (points == 0) return;
        if (!report.Groups.TryGetValue(groupName, out var g))
        {
            g = new GroupScore { Name = groupName };
            report.Groups[groupName] = g;
        }
        var entry = new ScoreEntry(rule, points, symbolName, groupName, file, line, detail);
        g.Entries.Add(entry);
        g.Total += points;
        g.ByRule[rule] = g.ByRule.GetValueOrDefault(rule) + points;
        report.Total += points;
        report.ByRule[rule] = report.ByRule.GetValueOrDefault(rule) + points;

        // Split into the two axes. Surface and internal complexity are tracked separately so
        // a baseline comparison can apply a Pareto gate instead of netting one against the other.
        if (SurfaceScoreRuleGroups.IsInternalComplexity(rule))
        {
            g.InternalComplexityTotal += points;
            report.InternalComplexityTotal += points;
        }
        else
        {
            g.SurfaceTotal += points;
            report.SurfaceTotal += points;
        }
    }
}
