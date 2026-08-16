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

        // A classification the config file declares but that ends up on no type switches its rules
        // off in silence, and the silence is worse than it looks: the merge is TryAdd, so declaring
        // a key REPLACES the default patterns for it. A block pointing at a directory that was
        // renamed away doesn't fall back — it classifies nothing, and every rule keyed to that tag
        // reads zero. Zero is indistinguishable from "clean" in the output, which is how a block
        // aimed at a project that has since been renamed away sits in a config indefinitely.
        // Only file-declared keys are reported: defaults legitimately match nothing on solutions
        // that have no repositories or no controllers, and warning about those would be noise on
        // every run.
        var deadClassifications = DeadClassifications(classified);
        if (deadClassifications.Count > 0)
            report.Diagnostics.Add(new ScoreDiagnostic("warning", "dead-config-classification",
                $"Config classifications match no type in this solution, so every rule keyed to them scores " +
                $"zero: {string.Join(", ", deadClassifications)}. Declaring a classification REPLACES the " +
                $"built-in patterns for that key rather than adding to them — check its " +
                $"paths/namespaces/namePatterns against the solution's actual layout, or delete the block to " +
                $"restore the defaults."));

        // A key no rule reads is inert whatever it matches — the classification typo that the
        // dead-classification check above cannot see, because the patterns may match plenty.
        var unknownClassifications = _config.DeclaredClassifications
            .Where(k => !SurfaceScoreConfig.KnownClassifications.Contains(k))
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();
        if (unknownClassifications.Count > 0)
            report.Diagnostics.Add(new ScoreDiagnostic("warning", "unknown-config-classification",
                $"Config declares classifications no rule reads, so they are ignored: {string.Join(", ", unknownClassifications)}. " +
                $"Readable classifications are: {string.Join(", ", SurfaceScoreConfig.KnownClassifications.OrderBy(k => k, StringComparer.Ordinal))}."));

        // The same trap on the weights table. This is what a retired rule leaves behind in a config
        // that tuned it: a number that reads like policy and is scored by nothing.
        var unknownWeights = _config.Weights.Keys
            .Where(k => !SurfaceScoreConfig.KnownWeights.Contains(k))
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();
        if (unknownWeights.Count > 0)
            report.Diagnostics.Add(new ScoreDiagnostic("warning", "unknown-config-weight",
                $"Config sets weights for rules that do not exist, so they do nothing: {string.Join(", ", unknownWeights)}. " +
                $"Either the name is misspelled or the rule was retired — the rule glossary printed " +
                $"with this report lists the names that exist."));

        // canonicalReadDtos is derived from each section's exported contracts surface now. A config
        // still carrying the list would otherwise change meaning in silence — it used to decide
        // which returns earned the canonicalReadDtoReturn credit.
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

        // Pass 4 — canonical read-DTO credit.
        ScoreReturnTypeRules(classified, report);

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

    /// <summary>
    /// File-declared classifications that landed on no type. Read off the tags the classifier
    /// actually assigned, not off pattern matching, so a block whose globs match only the wrong
    /// kind — a <c>readServiceInterface</c> whose names hit classes, say — reports here too: the
    /// classifier strips kind-inappropriate tags, and a stripped tag scores exactly like a tag that
    /// never matched. Keys no rule reads are left out; they get their own diagnostic, and reporting
    /// a typo twice helps nobody.
    /// </summary>
    private List<string> DeadClassifications(List<ClassifiedType> classified)
    {
        if (_config.DeclaredClassifications.Count == 0) return new List<string>();

        var applied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in classified)
            foreach (var tag in c.Tags)
                applied.Add(tag);

        return _config.DeclaredClassifications
            .Where(k => SurfaceScoreConfig.KnownClassifications.Contains(k) && !applied.Contains(k))
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();
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

    private void AddEntry(ScoreReport report, string groupName, string rule, int points,
        ISymbol symbol, string file, int line, string? detail)
    {
        var (scaled, multiplied) = ApplyContractsMultiplier(rule, points, symbol);
        AddEntryByName(report, groupName, rule, scaled, symbol.Name, file, line, detail,
            multiplied ? ScoreOrigin.Contracts : OriginOf(symbol), multiplied);
    }

    private static string OriginOf(ISymbol symbol) =>
        symbol.ContainingAssembly?.Name is { } asm && AssemblySections.IsContractsAssembly(asm)
            ? ScoreOrigin.Contracts
            : ScoreOrigin.Main;

    /// <summary>
    /// Scales a surface charge on a declaration in a satellite <c>&lt;Section&gt;.Contracts</c>
    /// assembly. The same type in a <c>Contracts/</c> folder inside the section's own assembly is
    /// not scaled: reaching it means referencing the whole assembly, while a satellite assembly can
    /// be referenced on its own — wider reach, and correspondingly harder to ever take back.
    /// </summary>
    /// <remarks>
    /// Two deliberate exclusions. <b>Credits are never scaled</b> — doubling a negative would turn
    /// the multiplier into a reward for publishing, which is the opposite of the intent. <b>The
    /// internal-complexity axis is never scaled</b> — it is the counterweight to surface and has to
    /// keep meaning "implementation this section carries", the same unit everywhere it is measured.
    /// </remarks>
    private (int Points, bool Multiplied) ApplyContractsMultiplier(string rule, int points, ISymbol symbol)
    {
        if (points <= 0) return (points, false);
        if (SurfaceScoreRuleGroups.IsInternalComplexity(rule)) return (points, false);
        if (OriginOf(symbol) != ScoreOrigin.Contracts) return (points, false);

        // <= 1 is a no-op rather than an error, and 0 in particular must not silently delete the
        // charge: a config typo should weaken the rule, never erase the surface it measures.
        var multiplier = _config.ContractsAssemblyMultiplier;
        if (multiplier <= 1) return (points, false);
        return (points * multiplier, true);
    }

    /// <summary>
    /// Section-level entries (missing surfaces, cross-section uses) aren't tied to a single
    /// declared symbol, so they carry an explicit name instead of an <see cref="ISymbol"/>.
    /// </summary>
    private static void AddEntryByName(ScoreReport report, string groupName, string rule, int points,
        string symbolName, string file, int line, string? detail,
        string origin = ScoreOrigin.Main, bool multiplied = false)
    {
        if (points == 0) return;
        if (!report.Groups.TryGetValue(groupName, out var g))
        {
            g = new GroupScore { Name = groupName };
            report.Groups[groupName] = g;
        }
        var entry = new ScoreEntry(rule, points, symbolName, groupName, file, line, detail)
        {
            Origin = origin,
            Multiplied = multiplied
        };
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
            if (origin == ScoreOrigin.Contracts) g.ContractsSurfaceTotal += points;
            else g.MainSurfaceTotal += points;
        }
    }
}
