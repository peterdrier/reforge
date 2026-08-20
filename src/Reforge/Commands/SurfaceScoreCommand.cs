using System.CommandLine;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Reforge.Commands;

/// <summary>
/// <c>surface-score</c> — scores the durable surface, dependency use, and internal
/// shape of every type in a C# solution. The command is generic: behaviour is driven
/// entirely by <c>reforge.surface-score.json</c> (sections, classifications, weights,
/// DbSet ownership). With no config it falls back to namespace-based grouping and
/// conventional name-pattern classifications.
/// </summary>
public static class SurfaceScoreCommand
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static Command Create(Option<string?> solutionOption, Option<OutputFormat> formatOption, Option<int?> limitOption)
    {
        var configOption = new Option<string?>("--config")
        {
            Description = "Path to reforge.surface-score.json (default: search upward from solution dir). The file defines `sections` (project-specific section/ownership map), classifications, weights, and DbSet ownership."
        };
        var groupOption = new Option<string?>("--group")
        {
            Description = "Restrict output to a single section/group. First checks configured sections (from --config or the discovered reforge.surface-score.json); then falls back to namespace-derived group names. Emits a diagnostic if the requested name matches nothing."
        };
        var topOption = new Option<int>("--top")
        {
            Description = "Number of top offenders to show per group (default 10). Pass 0 for no cap.",
            DefaultValueFactory = _ => 10
        };
        var allOption = new Option<bool>("--all")
        {
            Description = "Show every scored entry — alias for --top 0. Useful when an agent wants the full picture for combinatorial refactoring."
        };
        var topSymbolsOption = new Option<int>("--top-symbols")
        {
            Description = "Number of symbols to include in the cross-rule symbol aggregation (default 25). The view sums each symbol's points across every rule so combinations are visible.",
            DefaultValueFactory = _ => 25
        };
        var listGroupsOption = new Option<bool>("--list-groups")
        {
            Description = "List configured sections (from config) and groups discovered in the analysis, then exit."
        };
        var baselineOption = new Option<string?>("--baseline")
        {
            Description = "Path to a prior `surface-score --format json` output. Compares current surface and internal-complexity scores against it and applies a Pareto gate: a surface drop bought with a complexity rise is reported as a 'traded' verdict (not an improvement) plus a Suspicious Improvements section. Run per-commit (parent commit's JSON as baseline) to catch score-driven consolidation at the moment it happens."
        };
        var maxBuildDiagnosticsOption = new Option<int>("--max-build-diagnostics")
        {
            Description = "Cap on the number of individual compile errors listed when the workspace compile is degraded (default 25). 0 = unlimited. Only the listed detail is capped — the error/unresolved counts are always exact.",
            DefaultValueFactory = _ => 25
        };
        var allowDegradedOption = new Option<bool>("--allow-degraded")
        {
            Description = "Score even when the solution did not compile cleanly. Without this, a degraded build prints no score and exits 2, because a partial score reads as authoritative and has been quoted from broken trees before. With it, the score is printed, the result is marked degraded, and the exit code is 0."
        };

        var command = new Command("surface-score",
            "Score a solution's durable surface, dependency use, and internal shape (config-driven). Supports Compact, Markdown, and JSON output.")
        {
            configOption,
            groupOption,
            topOption,
            allOption,
            topSymbolsOption,
            listGroupsOption,
            baselineOption,
            maxBuildDiagnosticsOption,
            allowDegradedOption
        };

        command.SetAction(async (parseResult, ct) =>
        {
            var sw = Stopwatch.StartNew();
            var solutionPath = parseResult.GetValue(solutionOption);
            var configPath = parseResult.GetValue(configOption);
            var groupFilter = parseResult.GetValue(groupOption);
            var top = parseResult.GetValue(topOption);
            var all = parseResult.GetValue(allOption);
            var topSymbols = parseResult.GetValue(topSymbolsOption);
            var listGroups = parseResult.GetValue(listGroupsOption);
            var baselinePath = parseResult.GetValue(baselineOption);
            var maxBuildDiagnostics = parseResult.GetValue(maxBuildDiagnosticsOption);
            var allowDegraded = parseResult.GetValue(allowDegradedOption);
            var format = parseResult.GetValue(formatOption);

            // --all is an alias for --top 0 (no cap). int.MaxValue is the sentinel for "show
            // everything" through the Take(top) calls downstream.
            if (all || top == 0) top = int.MaxValue;

            var (solution, handle) = await WorkspaceHelper.OpenSolutionAsync(solutionPath);
            using (handle)
            {
                var solutionDir = LocationHelper.GetSolutionDirectory(solution);
                var config = SurfaceScoreConfig.LoadOrDefault(configPath, solutionDir, out var loadedFrom);

                var engine = new SurfaceScoreEngine(config, solutionDir);
                var report = await engine.ScoreAsync(solution, ct, maxBuildDiagnostics);
                report.ConfigPath = loadedFrom;

                // Build-health: when the analyzed solution did not compile cleanly the score is
                // partial, so by default nothing is printed and the command exits 2. The
                // diagnostics entry is still added first, so the --allow-degraded output carries
                // the same marker in every format that it always did.
                if (report.BuildHealth.Degraded)
                {
                    var buildMsg = BuildInspector.DescribeDegraded(report.BuildHealth);
                    report.Diagnostics.Add(new ScoreDiagnostic("warning", "degraded-build", buildMsg));

                    if (!allowDegraded)
                    {
                        // Return before any stdout write, including --list-groups: a section list
                        // read off a broken compilation is as misleading as a score, and one
                        // contract is easier to rely on than a per-flag exception.
                        sw.Stop();
                        Telemetry.Log("surface-score",
                            $"refused=degraded-build errors={report.BuildHealth.CompilationErrorCount} unresolved={report.BuildHealth.UnresolvedReferenceCount}",
                            0, sw.ElapsedMilliseconds);
                        return DegradedBuildGate.Refuse(report.BuildHealth, "surface-score", Console.Error);
                    }

                    DegradedBuildGate.Warn(report.BuildHealth, "surface-score", Console.Error);
                }

                BaselineComparison? baseline = null;
                if (baselinePath is not null)
                {
                    if (File.Exists(baselinePath))
                    {
                        baseline = SurfaceScoreBaseline.Compare(report, baselinePath);
                        report.SuspiciousImprovements.AddRange(baseline.Suspicious);
                        if (baseline.BaselineAnchorsMissing)
                            report.Diagnostics.Add(new ScoreDiagnostic("info", "baseline-anchors-missing",
                                "Baseline JSON predates conservationAnchors (v0.19+); conservation coverage degraded to ambiguous."));
                        if (baseline.BuildStateMismatch)
                            report.Diagnostics.Add(new ScoreDiagnostic("warning", "baseline-build-state-mismatch",
                                baseline.BuildStateMismatchMessage!));
                    }
                    else
                    {
                        report.Diagnostics.Add(new ScoreDiagnostic("warning", "baseline-not-found",
                            $"--baseline '{baselinePath}' does not exist; skipping Pareto comparison."));
                    }
                }

                // Build a missing-group diagnostic *before* writing output so every format
                // surfaces the same signal. Two distinct cases:
                //   - the name is not a section of this solution (no assembly of that name)
                //   - the section exists but scored nothing
                if (groupFilter is not null)
                {
                    var present = report.Groups.ContainsKey(groupFilter);
                    var known = report.ConfiguredSections.Contains(groupFilter, StringComparer.OrdinalIgnoreCase);
                    if (!present && !known)
                    {
                        report.Diagnostics.Add(new ScoreDiagnostic("warning", "group-not-found",
                            $"--group '{groupFilter}' is not a section of this solution. " +
                            $"Sections (one per assembly): {string.Join(", ", report.ConfiguredSections)}."));
                    }
                    else if (!present)
                    {
                        report.Diagnostics.Add(new ScoreDiagnostic("warning", "group-empty",
                            $"--group '{groupFilter}' is a section of this solution but scored no entries — " +
                            "every type in that assembly is unscored (e.g. pure DTOs that fail the data-carrier check)."));
                    }
                }

                if (listGroups)
                {
                    WriteGroupList(report, format);
                }
                else if (format == OutputFormat.Json)
                {
                    WriteJson(report, groupFilter, top, topSymbols, baseline);
                }
                else if (format == OutputFormat.Markdown)
                {
                    WriteMarkdown(report, groupFilter, top, topSymbols, baseline);
                }
                else
                {
                    WriteCompact(report, groupFilter, top, topSymbols, baseline);
                }

                sw.Stop();
                Telemetry.Log("surface-score",
                    $"groups={report.Groups.Count} types={report.TypesAnalyzed} filter={(groupFilter ?? "(all)")} cfg={(loadedFrom is null ? "default" : Path.GetFileName(loadedFrom))}",
                    report.Total, sw.ElapsedMilliseconds);

                return 0;
            }
        });

        return command;
    }

    // ----------------------- Symbol aggregation -----------------------

    private sealed record SymbolAggregate(string Symbol, string File, int Line, int Total, Dictionary<string, int> ByRule);

    /// <summary>
    /// Aggregates every score entry by symbol so an agent can see which refactoring targets
    /// carry combined value across multiple rules. A class hit by `writeCapableInterfaceUsedReadOnly`,
    /// `crossSectionRepository`, and `dashboardAdminPageName` is one fix worth ~43 points,
    /// but those entries sit in three different per-rule buckets in the grouped view. This sums them.
    /// File/line snap to the first entry's location for the symbol (close enough to navigate;
    /// individual rule entries still carry their own precise locations).
    /// </summary>
    private static List<SymbolAggregate> BuildSymbolAggregates(ScoreReport report, string? groupFilter, int max)
    {
        var byKey = new Dictionary<string, (string Symbol, string File, int Line, int Total, Dictionary<string, int> ByRule)>(StringComparer.Ordinal);

        foreach (var g in report.Groups.Values)
        {
            if (groupFilter is not null && !g.Name.Equals(groupFilter, StringComparison.OrdinalIgnoreCase))
                continue;

            foreach (var e in g.Entries)
            {
                // Key on symbol + file so two distinct classes with the same simple name don't merge.
                var key = $"{e.Symbol}|{e.File}";
                if (!byKey.TryGetValue(key, out var existing))
                {
                    existing = (e.Symbol, e.File, e.Line, 0, new Dictionary<string, int>(StringComparer.Ordinal));
                    byKey[key] = existing;
                }
                existing.Total += e.Points;
                existing.ByRule[e.Rule] = existing.ByRule.GetValueOrDefault(e.Rule) + e.Points;
                byKey[key] = existing;
            }
        }

        return byKey.Values
            // Sort by absolute total descending so positives and credits both surface. A symbol
            // sitting at -15 (heavy canonical-DTO adoption) is just as informative as +33.
            .OrderByDescending(v => Math.Abs(v.Total))
            .ThenBy(v => v.Symbol, StringComparer.Ordinal)
            .Take(max <= 0 ? int.MaxValue : max)
            .Select(v => new SymbolAggregate(v.Symbol, v.File, v.Line, v.Total, v.ByRule))
            .ToList();
    }

    // ----------------------- Compact (plain terse) -----------------------

    /// <summary>
    /// The captured workspace compile errors, one per line, in the spec format
    /// <c>  CSxxxx  &lt;path&gt;:&lt;line&gt;  &lt;message&gt;  (&lt;project&gt;)</c>, with a
    /// <c>(+N more)</c> footer when the cap truncated. Empty when the build is clean —
    /// so clean builds get no new output. Shared by the compact and markdown writers.
    /// </summary>
    internal static IEnumerable<string> BuildDiagnosticLines(BuildHealth bh)
    {
        if (!bh.Degraded || bh.Diagnostics.Count == 0) yield break;
        foreach (var d in bh.Diagnostics)
            yield return $"  {d.Id}  {d.File}:{d.Line}  {d.Message}  ({d.Project})";
        if (bh.DiagnosticsTruncated > 0)
            yield return $"  (+{bh.DiagnosticsTruncated} more)";
    }

    internal static void WriteCompact(ScoreReport report, string? groupFilter, int top, int topSymbols, BaselineComparison? baseline)
    {
        Console.WriteLine($"surface-score: surface={report.SurfaceTotal} internalComplexity={report.InternalComplexityTotal} combined={report.Total} (informational) types={report.TypesAnalyzed} groups={report.Groups.Count} config={(report.ConfigPath ?? "(defaults)")}");
        Console.WriteLine($"corpus{(groupFilter is null ? "" : $" ({groupFilter})")}: " +
                          $"{MetricsLine(ScopedMetrics(report, FilterAndOrderGroups(report, groupFilter), groupFilter))}");

        foreach (var d in report.Diagnostics)
            Console.WriteLine($"! {d.Level}: {d.Message}");

        // Under the degraded-build warning: the actual errors. Nothing when clean.
        foreach (var line in BuildDiagnosticLines(report.BuildHealth))
            Console.WriteLine(line);

        if (baseline is not null)
        {
            var s = baseline.Solution;
            var lowConfidence = baseline.BuildStateMismatch ? " lowConfidence=true" : "";
            Console.WriteLine();
            Console.WriteLine($"vs baseline ({Path.GetFileName(baseline.BaselinePath)}): verdict={s.Verdict} improvement={s.Improvement}{lowConfidence} " +
                              $"surface {s.BaseSurface}->{s.NowSurface} ({s.SurfaceDelta:+0;-0;0}) internalComplexity {s.BaseInternal}->{s.NowInternal} ({s.InternalDelta:+0;-0;0})");
            if (report.SuspiciousImprovements.Count > 0)
            {
                Console.WriteLine("Suspicious improvements:");
                foreach (var si in report.SuspiciousImprovements)
                    Console.WriteLine($"  ! [{si.Kind}] {si.Message}");
            }
        }

        var writeSurfaceLines = PublicWriteSurfaceLines(report, groupFilter, markdown: false).ToList();
        if (writeSurfaceLines.Count > 0)
        {
            Console.WriteLine();
            foreach (var line in writeSurfaceLines) Console.WriteLine(line);
        }

        var orderedGroups = FilterAndOrderGroups(report, groupFilter);
        if (orderedGroups.Count == 0)
        {
            if (report.Diagnostics.Count == 0)
                Console.WriteLine(groupFilter is null ? "(no scored items)" : $"(no items in group '{groupFilter}')");
            return;
        }

        // Rule glossary scoped to whatever the agent is looking at. With --group set, only
        // explain rules that fired in that section — otherwise the glossary lists rules the
        // agent's report doesn't actually contain.
        IReadOnlyDictionary<string, int> effectiveByRule = groupFilter is null
            ? (IReadOnlyDictionary<string, int>)report.ByRule
            : orderedGroups[0].ByRule;
        var firedRulesByScore = effectiveByRule.OrderByDescending(kv => kv.Value).Select(kv => kv.Key).ToList();
        var glossary = SurfaceScoreRuleGlossary.ForFiredRules(firedRulesByScore);
        if (glossary.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine(groupFilter is null ? "Rules:" : $"Rules (scoped to '{groupFilter}'):");
            foreach (var kv in glossary)
                Console.WriteLine($"  {kv.Key,-40} {kv.Value}");
        }

        // Section totals first as a one-liner per group, then per-group detail blocks.
        Console.WriteLine();
        foreach (var g in orderedGroups)
            Console.WriteLine($"  {g.Name,-30} {g.Total,5}  {MetricsSummary(g.Metrics)}");

        foreach (var g in orderedGroups)
        {
            Console.WriteLine();
            Console.WriteLine(g.ContractsSurfaceTotal != 0
                ? $"{g.Name} ({g.Total}; surface {g.MainSurfaceTotal} main + {g.ContractsSurfaceTotal} contracts)"
                : $"{g.Name} ({g.Total})");
            Console.WriteLine($"  {MetricsLine(g.Metrics)}");

            foreach (var kv in g.ByRule.OrderByDescending(x => x.Value).ThenBy(x => x.Key, StringComparer.Ordinal))
                Console.WriteLine($"  {kv.Key,-40} {kv.Value,5}");

            var topEntries = g.Entries
                .OrderByDescending(e => e.Points)
                .ThenBy(e => e.Rule, StringComparer.Ordinal)
                .Take(top <= 0 ? int.MaxValue : top)
                .ToList();
            if (topEntries.Count == 0) continue;

            Console.WriteLine();
            foreach (var e in topEntries)
            {
                var detail = string.IsNullOrEmpty(e.Detail) ? e.Symbol : e.Detail;
                var mark = e.Multiplied ? " [contracts]" : "";
                Console.WriteLine($"  {e.Points,3} {e.Rule,-35} {detail}{mark}  ({e.File}:{e.Line})");
            }
        }

        // Top-symbol combination view — surfaces refactoring targets that span multiple rules.
        var symbolAggs = BuildSymbolAggregates(report, groupFilter, topSymbols);
        if (symbolAggs.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"Top {symbolAggs.Count} symbols by total score (across rules):");
            Console.WriteLine();
            foreach (var s in symbolAggs)
            {
                var rules = string.Join(", ", s.ByRule
                    .OrderByDescending(kv => kv.Value)
                    .ThenBy(kv => kv.Key, StringComparer.Ordinal)
                    .Select(kv => $"{kv.Key}={kv.Value}"));
                Console.WriteLine($"  {s.Total,4} {s.Symbol,-40} {s.File}:{s.Line}");
                Console.WriteLine($"        {rules}");
            }
        }
    }

    // ----------------------- Markdown -----------------------

    internal static void WriteMarkdown(ScoreReport report, string? groupFilter, int top, int topSymbols, BaselineComparison? baseline)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Surface Score");
        sb.AppendLine();
        sb.AppendLine($"- **Surface Score**: {report.SurfaceTotal}");
        sb.AppendLine($"- **Internal Complexity Score**: {report.InternalComplexityTotal}");
        sb.AppendLine($"- **Combined Score** (informational, not an optimization target): {report.Total}");
        sb.AppendLine($"- **Types analyzed**: {report.TypesAnalyzed}");
        sb.AppendLine($"- **Corpus**{(groupFilter is null ? "" : $" (`{groupFilter}`)")}: " +
                      $"{MetricsLine(ScopedMetrics(report, FilterAndOrderGroups(report, groupFilter), groupFilter))}");
        sb.AppendLine($"- **Groups**: {report.Groups.Count}");
        sb.AppendLine($"- **Config**: {(report.ConfigPath ?? "(defaults, no reforge.surface-score.json found)")}");
        sb.AppendLine();

        if (baseline is not null)
            WriteBaselineMarkdown(sb, report, baseline);

        if (report.Diagnostics.Count > 0)
        {
            sb.AppendLine("## Diagnostics");
            sb.AppendLine();
            foreach (var d in report.Diagnostics)
                sb.AppendLine($"- **{d.Level}** (`{d.Code}`): {d.Message}");

            // The actual compile errors behind a degraded-build warning, fenced so the
            // alignment survives markdown rendering. Empty (and skipped) when clean.
            var buildLines = BuildDiagnosticLines(report.BuildHealth).ToList();
            if (buildLines.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("```");
                foreach (var line in buildLines)
                    sb.AppendLine(line);
                sb.AppendLine("```");
            }
            sb.AppendLine();
        }

        foreach (var line in PublicWriteSurfaceLines(report, groupFilter, markdown: true))
        {
            sb.AppendLine(line);
            sb.AppendLine();
        }

        var orderedGroups = FilterAndOrderGroups(report, groupFilter);

        if (orderedGroups.Count == 0)
        {
            sb.AppendLine(groupFilter is null
                ? "_No scored items found._"
                : $"_No items in group `{groupFilter}`._");
            Console.WriteLine(sb.ToString());
            return;
        }

        sb.AppendLine("## Totals by group");
        sb.AppendLine();
        sb.AppendLine("| Group | Score | LOC | Files | Classes | Interfaces | Cognitive p95 | Cognitive max | Max class LOC |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var g in orderedGroups)
        {
            var m = g.Metrics;
            sb.AppendLine($"| {g.Name} | {g.Total} | {m.LocProd} | {m.Files} | {m.Classes} | {m.Interfaces} | " +
                          $"{m.Cognitive.P95} | {m.Cognitive.Max} | {m.MaxClassLoc} |");
        }
        sb.AppendLine();

        // When the agent has filtered to one section, the solution-wide rule totals are
        // misleading (they include rules that fired outside the section). Scope the totals
        // table to the selected group; if no filter, keep the solution-wide view.
        var effectiveByRule = groupFilter is null
            ? (IReadOnlyDictionary<string, int>)report.ByRule
            : (orderedGroups.Count > 0 ? orderedGroups[0].ByRule : new Dictionary<string, int>());

        if (effectiveByRule.Count > 0)
        {
            sb.AppendLine(groupFilter is null ? "## Totals by rule" : $"## Totals by rule (scoped to `{groupFilter}`)");
            sb.AppendLine();
            sb.AppendLine("| Rule | Score | What it checks |");
            sb.AppendLine("|---|---:|---|");
            foreach (var kv in effectiveByRule.OrderByDescending(x => x.Value).ThenBy(x => x.Key, StringComparer.Ordinal))
            {
                var desc = SurfaceScoreRuleGlossary.Descriptions.TryGetValue(kv.Key, out var d) ? d : "";
                sb.AppendLine($"| `{kv.Key}` | {kv.Value} | {desc} |");
            }
            sb.AppendLine();
        }

        foreach (var g in orderedGroups)
        {
            sb.AppendLine($"## {g.Name} — surface {g.SurfaceTotal}, complexity {g.InternalComplexityTotal} (combined {g.Total})");
            sb.AppendLine();
            sb.AppendLine($"`{MetricsLine(g.Metrics)}`");
            sb.AppendLine();

            if (g.ByRule.Count > 0)
            {
                sb.AppendLine("| Rule | Score |");
                sb.AppendLine("|---|---:|");
                foreach (var kv in g.ByRule.OrderByDescending(x => x.Value).ThenBy(x => x.Key, StringComparer.Ordinal))
                    sb.AppendLine($"| `{kv.Key}` | {kv.Value} |");
                sb.AppendLine();
            }

            var topEntries = g.Entries
                .OrderByDescending(e => e.Points)
                .ThenBy(e => e.Rule, StringComparer.Ordinal)
                .Take(top <= 0 ? int.MaxValue : top)
                .ToList();
            if (topEntries.Count > 0)
            {
                sb.AppendLine($"### Top {topEntries.Count} offenders");
                sb.AppendLine();
                sb.AppendLine("| Points | Rule | Symbol | Location |");
                sb.AppendLine("|---:|---|---|---|");
                foreach (var e in topEntries)
                {
                    var symbolDisplay = string.IsNullOrEmpty(e.Detail) ? $"`{e.Symbol}`" : $"`{e.Detail}`";
                    if (e.Multiplied) symbolDisplay += " _(contracts)_";
                    sb.AppendLine($"| {e.Points} | `{e.Rule}` | {symbolDisplay} | `{e.File}:{e.Line}` |");
                }
                sb.AppendLine();
            }
        }

        // Cross-rule symbol aggregation — surfaces refactoring targets that span multiple rules.
        var symbolAggs = BuildSymbolAggregates(report, groupFilter, topSymbols);
        if (symbolAggs.Count > 0)
        {
            sb.AppendLine($"## Top {symbolAggs.Count} symbols by combined score");
            sb.AppendLine();
            sb.AppendLine("| Total | Symbol | Rules | Location |");
            sb.AppendLine("|---:|---|---|---|");
            foreach (var s in symbolAggs)
            {
                var rules = string.Join(", ", s.ByRule
                    .OrderByDescending(kv => kv.Value)
                    .ThenBy(kv => kv.Key, StringComparer.Ordinal)
                    .Select(kv => $"`{kv.Key}`={kv.Value}"));
                sb.AppendLine($"| {s.Total} | `{s.Symbol}` | {rules} | `{s.File}:{s.Line}` |");
            }
            sb.AppendLine();
        }

        if (groupFilter is null && report.DuplicateOwners.Count > 0)
        {
            sb.AppendLine("## Duplicate DbSet ownership");
            sb.AppendLine();
            foreach (var line in report.DuplicateOwners.OrderBy(s => s, StringComparer.Ordinal))
                sb.AppendLine($"- {line}");
            sb.AppendLine();
        }

        Console.WriteLine(sb.ToString());
    }

    private static void WriteBaselineMarkdown(StringBuilder sb, ScoreReport report, BaselineComparison baseline)
    {
        var s = baseline.Solution;
        var lowConfidence = baseline.BuildStateMismatch ? " — **LOW CONFIDENCE** (build-state mismatch; see Diagnostics)" : "";
        sb.AppendLine($"## Baseline comparison (`{Path.GetFileName(baseline.BaselinePath)}`)");
        sb.AppendLine();
        sb.AppendLine($"- **Verdict**: `{s.Verdict}` — **improvement: {s.Improvement}** (Pareto: surface non-worse AND complexity non-worse){lowConfidence}");
        sb.AppendLine($"- **Surface**: {s.BaseSurface} → {s.NowSurface} ({s.SurfaceDelta:+0;-0;0})");
        sb.AppendLine($"- **Internal complexity**: {s.BaseInternal} → {s.NowInternal} ({s.InternalDelta:+0;-0;0})");
        sb.AppendLine();

        if (report.SuspiciousImprovements.Count > 0)
        {
            sb.AppendLine("### Suspicious Improvements");
            sb.AppendLine();
            foreach (var si in report.SuspiciousImprovements)
                sb.AppendLine($"- **{si.Scope}** (`{si.Kind}`): {si.Message}");
            sb.AppendLine();
        }

        var changed = baseline.Groups
            .Where(g => g.SurfaceDelta != 0 || g.InternalDelta != 0)
            .OrderBy(g => g.Verdict == "traded" ? 0 : g.Verdict == "regressed" ? 1 : 2)
            .ThenByDescending(g => g.InternalDelta)
            .ToList();
        if (changed.Count > 0)
        {
            sb.AppendLine("### Per-group deltas");
            sb.AppendLine();
            sb.AppendLine("| Group | Verdict | Surface Δ | Complexity Δ |");
            sb.AppendLine("|---|---|---:|---:|");
            foreach (var g in changed)
                sb.AppendLine($"| {g.Scope} | `{g.Verdict}` | {g.SurfaceDelta:+0;-0;0} | {g.InternalDelta:+0;-0;0} |");
            sb.AppendLine();
        }
    }

    // ----------------------- JSON -----------------------

    internal static void WriteJson(ScoreReport report, string? groupFilter, int top, int topSymbols, BaselineComparison? baseline)
    {
        var filteredGroups = FilterAndOrderGroups(report, groupFilter);
        var effectiveTop = top <= 0 ? int.MaxValue : top;

        var groups = filteredGroups
            .Select(g => new
            {
                name = g.Name,
                surfaceTotal = g.SurfaceTotal,
                mainSurfaceTotal = g.MainSurfaceTotal,
                contractsSurfaceTotal = g.ContractsSurfaceTotal,
                internalComplexityTotal = g.InternalComplexityTotal,
                total = g.Total,
                metrics = MetricsJson(g.Metrics),
                byRule = g.ByRule
                    .OrderByDescending(kv => kv.Value)
                    .ThenBy(kv => kv.Key, StringComparer.Ordinal)
                    .ToDictionary(kv => kv.Key, kv => kv.Value),
                topEntries = g.Entries
                    .OrderByDescending(e => e.Points)
                    .ThenBy(e => e.Rule, StringComparer.Ordinal)
                    .Take(effectiveTop)
                    .Select(e => new
                    {
                        rule = e.Rule,
                        points = e.Points,
                        symbol = e.Symbol,
                        file = e.File,
                        line = e.Line,
                        detail = e.Detail,
                        origin = e.Origin,
                        multiplied = e.Multiplied
                    })
                    .ToArray()
            })
            .ToArray();

        // When --group is set, byRule and the rule glossary scope to that group so the
        // agent's view is self-contained. Solution-wide rule totals would otherwise mix in
        // counts from rules that fired outside the section.
        IReadOnlyDictionary<string, int> effectiveByRule = groupFilter is null
            ? report.ByRule
            : (filteredGroups.Count > 0 ? filteredGroups[0].ByRule : new Dictionary<string, int>());

        var firedRules = effectiveByRule
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => kv.Key)
            .ToList();
        var ruleGlossary = SurfaceScoreRuleGlossary.ForFiredRules(firedRules)
            .ToDictionary(kv => kv.Key, kv => kv.Value);

        var symbolAggs = BuildSymbolAggregates(report, groupFilter, topSymbols)
            .Select(s => new
            {
                symbol = s.Symbol,
                file = s.File,
                line = s.Line,
                total = s.Total,
                byRule = s.ByRule
                    .OrderByDescending(kv => kv.Value)
                    .ThenBy(kv => kv.Key, StringComparer.Ordinal)
                    .ToDictionary(kv => kv.Key, kv => kv.Value)
            })
            .ToArray();

        object? baselinePayload = baseline is null ? null : new
        {
            path = baseline.BaselinePath,
            verdict = baseline.Solution.Verdict,
            improvement = baseline.Solution.Improvement,
            // Additive: only present (true) when the baseline and current build states differ —
            // null (and so omitted by WhenWritingNull) on a matched comparison, so a clean-vs-clean
            // run stays byte-identical to pre-guard output. Detail lives in `diagnostics`.
            lowConfidence = baseline.BuildStateMismatch ? true : (bool?)null,
            surface = new { @base = baseline.Solution.BaseSurface, now = baseline.Solution.NowSurface, delta = baseline.Solution.SurfaceDelta },
            internalComplexity = new { @base = baseline.Solution.BaseInternal, now = baseline.Solution.NowInternal, delta = baseline.Solution.InternalDelta },
            groups = baseline.Groups
                .Where(g => g.SurfaceDelta != 0 || g.InternalDelta != 0)
                .Select(g => new { scope = g.Scope, verdict = g.Verdict, improvement = g.Improvement, surfaceDelta = g.SurfaceDelta, internalDelta = g.InternalDelta })
                .ToArray()
        };

        var payload = new
        {
            command = "surface-score",
            total = report.Total,
            surfaceTotal = report.SurfaceTotal,
            internalComplexityTotal = report.InternalComplexityTotal,
            combinedTotal = report.Total,
            typesAnalyzed = report.TypesAnalyzed,
            // Scoped like byRule below: with --group set, the solution-wide corpus would read as
            // the section's. `scope` says which one this is.
            metrics = MetricsJson(ScopedMetrics(report, filteredGroups, groupFilter)),
            publicWriteSurface = PublicWriteSurfaceJson(report, groupFilter),
            build = new
            {
                degraded = report.BuildHealth.Degraded,
                compilationErrorCount = report.BuildHealth.CompilationErrorCount,
                unresolvedReferenceCount = report.BuildHealth.UnresolvedReferenceCount,
                appearsUnbuilt = report.BuildHealth.AppearsUnbuilt,
                // Additive: the captured errors and the cap-truncation count. Null (and so
                // omitted by WhenWritingNull) on a clean build — no new output when clean.
                diagnostics = report.BuildHealth.Degraded
                    ? report.BuildHealth.Diagnostics.Select(d => new
                    {
                        id = d.Id,
                        severity = d.Severity,
                        project = d.Project,
                        file = d.File,
                        line = d.Line,
                        message = d.Message
                    }).ToArray()
                    : null,
                diagnosticsTruncated = report.BuildHealth.Degraded
                    ? report.BuildHealth.DiagnosticsTruncated
                    : (int?)null
            },
            configPath = report.ConfigPath,
            configuredSections = report.ConfiguredSections,
            scope = groupFilter is null ? "solution" : $"group:{groupFilter}",
            byRule = effectiveByRule
                .OrderByDescending(kv => kv.Value)
                .ThenBy(kv => kv.Key, StringComparer.Ordinal)
                .ToDictionary(kv => kv.Key, kv => kv.Value),
            ruleGlossary,
            groups,
            topSymbols = symbolAggs,
            duplicateOwners = report.DuplicateOwners,
            baseline = baselinePayload,
            suspiciousImprovements = report.SuspiciousImprovements
                .Select(si => new { scope = si.Scope, kind = si.Kind, message = si.Message, surfaceDelta = si.SurfaceDelta, internalDelta = si.InternalDelta, improvement = si.Improvement })
                .ToArray(),
            diagnostics = report.Diagnostics.Select(d => new { level = d.Level, code = d.Code, message = d.Message }).ToArray(),
            conservationAnchors = report.ConservationAnchors.Select(a => new
            {
                key = a.Key,
                section = a.Section,
                role = a.Role,
                paths = a.Paths,
                methods = a.Methods.Select(m => new { name = m.Name, returns = m.Returns }).ToArray(),
                byRule = a.ByRule
            }).ToArray(),
            helperCandidates = report.HelperCandidates.Select(h => new { display = h.Display, methods = h.Methods }).ToArray(),
            conservationEvidence = baseline is null ? Array.Empty<object>() : baseline.ConservationVerdicts.Select(v => new
            {
                section = v.Section,
                kind = v.Kind,
                improvement = v.Improvement,
                message = v.Message,
                methods = v.Methods.Select(m => new
                {
                    removedMethod = m.RemovedMethod,
                    coverageKind = m.CoverageKind,
                    targetDto = m.TargetDto,
                    coveredBy = m.CoveredBy,
                    missingInfoFacts = m.MissingInfoFacts.Select(f => new { fact = f.Fact, targetDto = f.TargetDto }).ToArray()
                }).ToArray()
            }).ToArray<object>()
        };

        Console.WriteLine(JsonSerializer.Serialize(payload, JsonOptions));
    }

    // ----------------------- Public write surface (reported, never scored) -----------------------

    /// <summary>
    /// Publishing sections out of all sections. Keyed off <see cref="AllSections"/>, not the scored
    /// groups: a section whose published interface charges nothing has no group at all, and that is
    /// the case most worth seeing.
    /// </summary>
    private static (int Sections, string[] Publishing, int Interfaces) PublicWriteSurface(
        ScoreReport report, string? groupFilter)
    {
        var sections = groupFilter is null
            ? AllSections(report).Select(s => s.Name).ToList()
            : new List<string> { groupFilter };

        var publishing = sections
            .Where(s => report.PublicWriteSurface.TryGetValue(s, out var p) && p.Count > 0)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToArray();

        return (sections.Count, publishing, publishing.Sum(s => report.PublicWriteSurface[s].Count));
    }

    private static string[] Published(ScoreReport report, string section) =>
        report.PublicWriteSurface.TryGetValue(section, out var published)
            ? published.OrderBy(n => n, StringComparer.Ordinal).ToArray()
            : Array.Empty<string>();

    private static object PublicWriteSurfaceJson(ScoreReport report, string? groupFilter)
    {
        var (sections, publishing, interfaces) = PublicWriteSurface(report, groupFilter);
        return new
        {
            sections,
            publishingSections = publishing.Length,
            interfaces,
            bySection = publishing.ToDictionary(s => s, s => Published(report, s), StringComparer.Ordinal)
        };
    }

    private static IEnumerable<string> PublicWriteSurfaceLines(ScoreReport report, string? groupFilter, bool markdown)
    {
        var (sections, publishing, interfaces) = PublicWriteSurface(report, groupFilter);
        if (publishing.Length == 0) yield break;

        yield return markdown
            ? $"## publicWriteSurface — {publishing.Length} of {sections} sections publish write capability ({interfaces} interfaces, reported and unscored)"
            : $"publicWriteSurface (reported, unscored): {publishing.Length}/{sections} sections, {interfaces} interfaces";
        foreach (var section in publishing)
            yield return markdown
                ? $"- `{section}`: {string.Join(", ", Published(report, section).Select(n => $"`{n}`"))}"
                : $"  {section}: {string.Join(", ", Published(report, section))}";
    }

    // ----------------------- Metrics -----------------------

    /// <summary>
    /// The corpus the reader is actually looking at: the solution's, or — when <c>--group</c> is
    /// set — that one section's. Mirrors how <c>byRule</c> and the rule glossary scope, for the
    /// same reason: a report filtered to one section that quotes solution-wide numbers beside it
    /// invites reading them as the section's.
    /// </summary>
    private static SectionMetrics ScopedMetrics(ScoreReport report, List<GroupScore> filteredGroups, string? groupFilter)
    {
        if (groupFilter is null) return report.Metrics;
        // A section can exist with no scored entries and so no group; its metrics are still known.
        if (report.MetricsBySection.TryGetValue(groupFilter, out var m)) return m;
        return filteredGroups.Count > 0 ? filteredGroups[0].Metrics : SectionMetrics.Empty;
    }

    /// <summary>
    /// Size/complexity of a scope, for the JSON report. Emitted for every group and once for the
    /// solution. Informational — no key here participates in a score, so a consumer diffing totals
    /// can ignore the whole object.
    /// </summary>
    private static object MetricsJson(SectionMetrics m) => new
    {
        locProd = m.LocProd,
        files = m.Files,
        classes = m.Classes,
        interfaces = m.Interfaces,
        methods = m.Methods,
        cognitive = DistributionJson(m.Cognitive),
        cyclomatic = DistributionJson(m.Cyclomatic),
        maxClassLoc = m.MaxClassLoc,
        maxClassLocName = m.MaxClassLocName
    };

    private static object DistributionJson(MetricDistribution d) => new
    {
        avg = d.Avg,
        p95 = d.P95,
        max = d.Max,
        maxMethod = d.MaxMethod
    };

    /// <summary>
    /// One-line size/complexity summary for the compact and markdown reports. Cognitive is the
    /// figure carried inline because it is the metric the internal-complexity axis actually
    /// scores; cyclomatic is in the JSON for continuity with the <c>snapshot</c> history series.
    /// </summary>
    private static string MetricsLine(SectionMetrics m) =>
        $"loc={m.LocProd} files={m.Files} classes={m.Classes} interfaces={m.Interfaces} " +
        $"methods={m.Methods} cognitive avg={m.Cognitive.Avg} p95={m.Cognitive.P95} max={m.Cognitive.Max}" +
        (string.IsNullOrEmpty(m.Cognitive.MaxMethod) ? "" : $" ({m.Cognitive.MaxMethod})") +
        $" maxClassLoc={m.MaxClassLoc}" +
        (string.IsNullOrEmpty(m.MaxClassLocName) ? "" : $" ({m.MaxClassLocName})");

    /// <summary>Terser form for the per-section summary list, where one line per section is the budget.</summary>
    private static string MetricsSummary(SectionMetrics m) =>
        $"loc={m.LocProd} cogP95={m.Cognitive.P95} cogMax={m.Cognitive.Max}";

    // ----------------------- --list-groups -----------------------

    /// <summary>
    /// Every section of the solution with its score and size, including sections that scored
    /// nothing. A section whose types are all unscored (pure DTOs that fail the data-carrier
    /// check, say) has metrics but no <see cref="GroupScore"/> — listing only the scored groups
    /// would drop it, and it is exactly the section a size-ranked listing needs to show.
    /// </summary>
    private static List<(string Name, int Total, int Entries, SectionMetrics Metrics)> AllSections(ScoreReport report)
    {
        var names = new HashSet<string>(report.ConfiguredSections, StringComparer.OrdinalIgnoreCase);
        foreach (var name in report.Groups.Keys) names.Add(name);

        return names
            .Select(name =>
            {
                var total = report.Groups.TryGetValue(name, out var g) ? g.Total : 0;
                var entries = g?.Entries.Count ?? 0;
                var metrics = report.MetricsBySection.TryGetValue(name, out var m) ? m : SectionMetrics.Empty;
                return (Name: name, Total: total, Entries: entries, Metrics: metrics);
            })
            .OrderByDescending(s => s.Total)
            .ThenBy(s => s.Name, StringComparer.Ordinal)
            .ToList();
    }

    internal static void WriteGroupListForTest(ScoreReport report, OutputFormat format)
        => WriteGroupList(report, format);

    private static void WriteGroupList(ScoreReport report, OutputFormat format)
    {
        if (format == OutputFormat.Json)
        {
            var payload = new
            {
                command = "surface-score",
                listGroups = true,
                configuredSections = report.ConfiguredSections,
                // Every section, scored or not, with its size — the list to rank by. `discoveredGroups`
                // below stays what it has always been (sections that scored) so consumers reading it
                // are unaffected.
                sections = AllSections(report)
                    .Select(s => new { name = s.Name, total = s.Total, entries = s.Entries, locProd = s.Metrics.LocProd })
                    .ToArray(),
                discoveredGroups = report.Groups
                    .OrderByDescending(kv => kv.Value.Total)
                    .ThenBy(kv => kv.Key, StringComparer.Ordinal)
                    .Select(kv => new
                    {
                        name = kv.Key,
                        total = kv.Value.Total,
                        entries = kv.Value.Entries.Count,
                        locProd = kv.Value.Metrics.LocProd
                    })
                    .ToArray()
            };
            Console.WriteLine(JsonSerializer.Serialize(payload, JsonOptions));
            return;
        }

        var sections = AllSections(report);
        Console.WriteLine($"Sections ({sections.Count}, one per assembly):");
        foreach (var s in sections)
            Console.WriteLine($"  {s.Name,-30} total={s.Total,5} entries={s.Entries} loc={s.Metrics.LocProd}");

        Console.WriteLine();
        Console.WriteLine($"Discovered groups ({report.Groups.Count}):");
        foreach (var kv in report.Groups
            .OrderByDescending(kv => kv.Value.Total)
            .ThenBy(kv => kv.Key, StringComparer.Ordinal))
        {
            Console.WriteLine($"  {kv.Key,-30} total={kv.Value.Total,5} entries={kv.Value.Entries.Count} loc={kv.Value.Metrics.LocProd}");
        }
    }

    private static List<GroupScore> FilterAndOrderGroups(ScoreReport report, string? groupFilter) =>
        report.Groups.Values
            .Where(g => groupFilter is null || g.Name.Equals(groupFilter, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(g => g.Total)
            .ThenBy(g => g.Name, StringComparer.Ordinal)
            .ToList();
}
