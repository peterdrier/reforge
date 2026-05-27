using System.CommandLine;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Reforge.Commands;

/// <summary>
/// <c>surface-score</c> — scores the durable surface, dependency use, and internal
/// shape of every type in a C# solution. The command is generic: behaviour is driven
/// entirely by <c>reforge.surface-score.json</c> (groups, classifications, weights,
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
            Description = "Path to reforge.surface-score.json (default: search upward from solution dir)"
        };
        var groupOption = new Option<string?>("--group")
        {
            Description = "Restrict output to a single configured group"
        };
        var topOption = new Option<int>("--top")
        {
            Description = "Number of top offenders to show per group (default 10)",
            DefaultValueFactory = _ => 10
        };

        var command = new Command("surface-score",
            "Score a solution's durable surface, dependency use, and internal shape (config-driven). Markdown output.")
        {
            configOption,
            groupOption,
            topOption
        };

        command.SetAction(async (parseResult, ct) =>
        {
            var sw = Stopwatch.StartNew();
            var solutionPath = parseResult.GetValue(solutionOption);
            var configPath = parseResult.GetValue(configOption);
            var groupFilter = parseResult.GetValue(groupOption);
            var top = parseResult.GetValue(topOption);
            var format = parseResult.GetValue(formatOption);

            var (solution, handle) = await WorkspaceHelper.OpenSolutionAsync(solutionPath);
            using (handle)
            {
                var solutionDir = LocationHelper.GetSolutionDirectory(solution);
                var config = SurfaceScoreConfig.LoadOrDefault(configPath, solutionDir, out var loadedFrom);

                var engine = new SurfaceScoreEngine(config, solutionDir);
                var report = await engine.ScoreAsync(solution, ct);
                report.ConfigPath = loadedFrom;

                if (format == OutputFormat.Json)
                    WriteJson(report, groupFilter, top);
                else
                    WriteMarkdown(report, groupFilter, top);

                sw.Stop();
                Telemetry.Log("surface-score",
                    $"groups={report.Groups.Count} types={report.TypesAnalyzed} cfg={(loadedFrom is null ? "default" : Path.GetFileName(loadedFrom))}",
                    report.Total, sw.ElapsedMilliseconds);
            }
        });

        return command;
    }

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

        // Per-group totals table.
        var orderedGroups = report.Groups.Values
            .Where(g => groupFilter is null || g.Name.Equals(groupFilter, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(g => g.Total)
            .ThenBy(g => g.Name, StringComparer.Ordinal)
            .ToList();

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

        // Per-rule totals table (solution-wide). Useful to see at a glance what's
        // dominating the score (e.g. lots of cross-section repos).
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

        // Per-group breakdown.
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

            // Top offenders: heaviest individual entries.
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

        // Resource-ownership callouts are surfaced separately because they signal a
        // boundary violation, not just a heavy weight.
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

    private static void WriteJson(ScoreReport report, string? groupFilter, int top)
    {
        var groups = report.Groups.Values
            .Where(g => groupFilter is null || g.Name.Equals(groupFilter, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(g => g.Total)
            .ThenBy(g => g.Name, StringComparer.Ordinal)
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
            byRule = report.ByRule
                .OrderByDescending(kv => kv.Value)
                .ThenBy(kv => kv.Key, StringComparer.Ordinal)
                .ToDictionary(kv => kv.Key, kv => kv.Value),
            groups,
            duplicateOwners = report.DuplicateOwners
        };

        Console.WriteLine(JsonSerializer.Serialize(payload, JsonOptions));
    }
}
