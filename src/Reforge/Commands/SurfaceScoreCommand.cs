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
            Description = "Number of top offenders to show per group (default 10)",
            DefaultValueFactory = _ => 10
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
            listGroupsOption
        };

        command.SetAction(async (parseResult, ct) =>
        {
            var sw = Stopwatch.StartNew();
            var solutionPath = parseResult.GetValue(solutionOption);
            var configPath = parseResult.GetValue(configOption);
            var groupFilter = parseResult.GetValue(groupOption);
            var top = parseResult.GetValue(topOption);
            var listGroups = parseResult.GetValue(listGroupsOption);
            var format = parseResult.GetValue(formatOption);

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
                    WriteJson(report, groupFilter, top);
                }
                else if (format == OutputFormat.Markdown)
                {
                    WriteMarkdown(report, groupFilter, top);
                }
                else
                {
                    WriteCompact(report, groupFilter, top);
                }

                sw.Stop();
                Telemetry.Log("surface-score",
                    $"groups={report.Groups.Count} types={report.TypesAnalyzed} filter={(groupFilter ?? "(all)")} cfg={(loadedFrom is null ? "default" : Path.GetFileName(loadedFrom))}",
                    report.Total, sw.ElapsedMilliseconds);
            }
        });

        return command;
    }

    // ----------------------- Compact (plain terse) -----------------------

    private static void WriteCompact(ScoreReport report, string? groupFilter, int top)
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
                .Take(top)
                .ToList();
            if (topEntries.Count == 0) continue;

            Console.WriteLine();
            foreach (var e in topEntries)
            {
                var detail = string.IsNullOrEmpty(e.Detail) ? e.Symbol : e.Detail;
                Console.WriteLine($"  {e.Points,3} {e.Rule,-35} {detail}  ({e.File}:{e.Line})");
            }
        }
    }

    // ----------------------- Markdown -----------------------

    private static void WriteMarkdown(ScoreReport report, string? groupFilter, int top)
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

        if (groupFilter is null && report.ByRule.Count > 0)
        {
            sb.AppendLine("## Totals by rule");
            sb.AppendLine();
            sb.AppendLine("| Rule | Score |");
            sb.AppendLine("|---|---:|");
            foreach (var kv in report.ByRule.OrderByDescending(x => x.Value).ThenBy(x => x.Key, StringComparer.Ordinal))
                sb.AppendLine($"| `{kv.Key}` | {kv.Value} |");
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
                .Take(top)
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

    private static void WriteJson(ScoreReport report, string? groupFilter, int top)
    {
        var groups = FilterAndOrderGroups(report, groupFilter)
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
                    .Take(top)
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

        var payload = new
        {
            command = "surface-score",
            total = report.Total,
            typesAnalyzed = report.TypesAnalyzed,
            configPath = report.ConfigPath,
            configuredSections = report.ConfiguredSections,
            byRule = report.ByRule
                .OrderByDescending(kv => kv.Value)
                .ThenBy(kv => kv.Key, StringComparer.Ordinal)
                .ToDictionary(kv => kv.Key, kv => kv.Value),
            groups,
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
