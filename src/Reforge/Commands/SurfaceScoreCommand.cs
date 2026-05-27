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

        var command = new Command("surface-score",
            "Score a solution's durable surface, dependency use, and internal shape (config-driven). Supports Compact, Markdown, and JSON output.")
        {
            configOption,
            groupOption,
            topOption,
            allOption,
            topSymbolsOption,
            listGroupsOption
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
                var report = await engine.ScoreAsync(solution, ct);
                report.ConfigPath = loadedFrom;

                // Build a missing-group diagnostic *before* writing output so every format
                // surfaces the same signal. Two distinct cases:
                //   - the name doesn't match any configured section AND isn't a known group
                //   - the section is configured but matched zero types
                if (groupFilter is not null)
                {
                    var present = report.Groups.ContainsKey(groupFilter);
                    var configured = config.HasConfiguredSection(groupFilter);
                    if (!present && !configured)
                    {
                        report.Diagnostics.Add(new ScoreDiagnostic("warning", "group-not-found",
                            $"--group '{groupFilter}' did not match any configured section or discovered group. " +
                            (report.ConfiguredSections.Count > 0
                                ? $"Configured sections: {string.Join(", ", report.ConfiguredSections)}. "
                                : "No sections configured. ") +
                            $"Discovered groups: {string.Join(", ", report.Groups.Keys.OrderBy(k => k, StringComparer.Ordinal))}."));
                    }
                    else if (!present && configured)
                    {
                        report.Diagnostics.Add(new ScoreDiagnostic("warning", "group-empty",
                            $"--group '{groupFilter}' is configured but matched no scored entries. " +
                            "Likely causes: paths/symbols/namespaces in the config don't match the actual layout, " +
                            "or every type in the section is unscored (e.g. pure DTOs that fail the data-carrier check)."));
                    }
                }

                if (listGroups)
                {
                    WriteGroupList(report, format);
                }
                else if (format == OutputFormat.Json)
                {
                    WriteJson(report, groupFilter, top, topSymbols);
                }
                else if (format == OutputFormat.Markdown)
                {
                    WriteMarkdown(report, groupFilter, top, topSymbols);
                }
                else
                {
                    WriteCompact(report, groupFilter, top, topSymbols);
                }

                sw.Stop();
                Telemetry.Log("surface-score",
                    $"groups={report.Groups.Count} types={report.TypesAnalyzed} filter={(groupFilter ?? "(all)")} cfg={(loadedFrom is null ? "default" : Path.GetFileName(loadedFrom))}",
                    report.Total, sw.ElapsedMilliseconds);
            }
        });

        return command;
    }

    // ----------------------- Symbol aggregation -----------------------

    private sealed record SymbolAggregate(string Symbol, string File, int Line, int Total, Dictionary<string, int> ByRule);

    /// <summary>
    /// Aggregates every score entry by symbol so an agent can see which refactoring targets
    /// carry combined value across multiple rules. A class hit by `writeCapableInterfaceUsedReadOnly`,
    /// `methodReturnsEntityAcrossSection`, and `dashboardAdminPageName` is one fix worth ~33 points,
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

    private static void WriteCompact(ScoreReport report, string? groupFilter, int top, int topSymbols)
    {
        Console.WriteLine($"surface-score: total={report.Total} types={report.TypesAnalyzed} groups={report.Groups.Count} config={(report.ConfigPath ?? "(defaults)")}");

        foreach (var d in report.Diagnostics)
            Console.WriteLine($"! {d.Level}: {d.Message}");

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
            Console.WriteLine($"  {g.Name,-30} {g.Total,5}");

        foreach (var g in orderedGroups)
        {
            Console.WriteLine();
            Console.WriteLine($"{g.Name} ({g.Total})");

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
                Console.WriteLine($"  {e.Points,3} {e.Rule,-35} {detail}  ({e.File}:{e.Line})");
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

    private static void WriteMarkdown(ScoreReport report, string? groupFilter, int top, int topSymbols)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Surface Score");
        sb.AppendLine();
        sb.AppendLine($"- **Total**: {report.Total}");
        sb.AppendLine($"- **Types analyzed**: {report.TypesAnalyzed}");
        sb.AppendLine($"- **Groups**: {report.Groups.Count}");
        sb.AppendLine($"- **Config**: {(report.ConfigPath ?? "(defaults, no reforge.surface-score.json found)")}");
        sb.AppendLine();

        if (report.Diagnostics.Count > 0)
        {
            sb.AppendLine("## Diagnostics");
            sb.AppendLine();
            foreach (var d in report.Diagnostics)
                sb.AppendLine($"- **{d.Level}** (`{d.Code}`): {d.Message}");
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
        sb.AppendLine("| Group | Score |");
        sb.AppendLine("|---|---:|");
        foreach (var g in orderedGroups)
            sb.AppendLine($"| {g.Name} | {g.Total} |");
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
            sb.AppendLine($"## {g.Name} — {g.Total}");
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

    // ----------------------- JSON -----------------------

    private static void WriteJson(ScoreReport report, string? groupFilter, int top, int topSymbols)
    {
        var filteredGroups = FilterAndOrderGroups(report, groupFilter);
        var effectiveTop = top <= 0 ? int.MaxValue : top;

        var groups = filteredGroups
            .Select(g => new
            {
                name = g.Name,
                total = g.Total,
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
                        detail = e.Detail
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

        var payload = new
        {
            command = "surface-score",
            total = report.Total,
            typesAnalyzed = report.TypesAnalyzed,
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
            diagnostics = report.Diagnostics.Select(d => new { level = d.Level, code = d.Code, message = d.Message }).ToArray()
        };

        Console.WriteLine(JsonSerializer.Serialize(payload, JsonOptions));
    }

    // ----------------------- --list-groups -----------------------

    private static void WriteGroupList(ScoreReport report, OutputFormat format)
    {
        if (format == OutputFormat.Json)
        {
            var payload = new
            {
                command = "surface-score",
                listGroups = true,
                configuredSections = report.ConfiguredSections,
                discoveredGroups = report.Groups
                    .OrderByDescending(kv => kv.Value.Total)
                    .ThenBy(kv => kv.Key, StringComparer.Ordinal)
                    .Select(kv => new { name = kv.Key, total = kv.Value.Total, entries = kv.Value.Entries.Count })
                    .ToArray()
            };
            Console.WriteLine(JsonSerializer.Serialize(payload, JsonOptions));
            return;
        }

        Console.WriteLine($"Configured sections ({report.ConfiguredSections.Count}):");
        if (report.ConfiguredSections.Count == 0)
            Console.WriteLine("  (none — using namespace-fallback grouping)");
        else
            foreach (var s in report.ConfiguredSections)
                Console.WriteLine($"  {s}");

        Console.WriteLine();
        Console.WriteLine($"Discovered groups ({report.Groups.Count}):");
        foreach (var kv in report.Groups
            .OrderByDescending(kv => kv.Value.Total)
            .ThenBy(kv => kv.Key, StringComparer.Ordinal))
        {
            Console.WriteLine($"  {kv.Key,-30} total={kv.Value.Total,5} entries={kv.Value.Entries.Count}");
        }
    }

    private static List<GroupScore> FilterAndOrderGroups(ScoreReport report, string? groupFilter) =>
        report.Groups.Values
            .Where(g => groupFilter is null || g.Name.Equals(groupFilter, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(g => g.Total)
            .ThenBy(g => g.Name, StringComparer.Ordinal)
            .ToList();
}
