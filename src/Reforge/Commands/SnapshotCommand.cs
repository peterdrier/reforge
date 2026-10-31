using System.CommandLine;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Reforge.Commands;

public static class SnapshotCommand
{
    public static Command Create(Option<string?> solutionOption, Option<OutputFormat> formatOption, Option<int?> limitOption)
    {
        var appendOption = new Option<string?>("--append")
        {
            Description = "Append one CSV row to this file (writes header if file doesn't exist). Forces --format csv."
        };

        var byAssemblyOption = new Option<bool>("--by-assembly")
        {
            Description = "One row per section (assembly) plus a solution rollup, with size, complexity, " +
                          "exported/internal x read/write surface counts, tokens, size charges and the test value. CSV unless --format json."
        };

        var coverageOption = new Option<string?>("--coverage")
        {
            Description = "With --by-assembly: a Cobertura or dotCover DetailedXML report whose statement coverage is attributed to each section by assembly."
        };

        var scoreOption = new Option<bool>("--score")
        {
            Description = "With --by-assembly: fill score_surface, score_implementation and score_total by running the full " +
                          "surface-score engine, about twice the time of a run without it. Every other column is filled either way."
        };

        var countTokensOption = new Option<bool>("--count-tokens")
        {
            Description = "With --by-assembly: count exact tokens for files the cache lacks, as surface-score --count-tokens does. " +
                          "Cached exact counts are used without it; anything uncounted is estimated."
        };

        var tokenCacheOption = new Option<string?>("--token-cache")
        {
            Description = "Token cache file, shared with surface-score (default: $REFORGE_TOKEN_CACHE, else <LocalApplicationData>/reforge/token-cache.txt)."
        };

        var command = new Command("snapshot", "One-row macro-scale code health record for time-series charting")
        {
            appendOption,
            byAssemblyOption,
            coverageOption,
            scoreOption,
            countTokensOption,
            tokenCacheOption
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var solutionPath = parseResult.GetValue(solutionOption);
            var format = parseResult.GetValue(formatOption);
            var appendPath = parseResult.GetValue(appendOption);
            var byAssembly = parseResult.GetValue(byAssemblyOption);
            var coveragePath = parseResult.GetValue(coverageOption);
            var sw = Stopwatch.StartNew();

            var (solution, handle) = await WorkspaceHelper.OpenSolutionAsync(solutionPath);
            using (handle)
            {
                if (byAssembly)
                {
                    using var tokens = TokenSession.Open(parseResult.GetValue(countTokensOption), parseResult.GetValue(tokenCacheOption), Console.Error);
                    var rows = await SectionSnapshotAnalyzer.AnalyzeAsync(solution, coveragePath, cancellationToken,
                        score: parseResult.GetValue(scoreOption), tokens: tokens.Measurer);
                    tokens.Save(Console.Error);
                    if (appendPath is not null)
                    {
                        SectionSnapshotAnalyzer.AppendCsv(rows, appendPath);
                        Console.WriteLine($"appended {rows.Count} rows to {appendPath}");
                    }
                    else if (format == OutputFormat.Json)
                    {
                        Console.WriteLine(JsonSerializer.Serialize(rows, new JsonSerializerOptions
                        {
                            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                            WriteIndented = true
                        }));
                    }
                    else
                    {
                        Console.Write(SectionSnapshotAnalyzer.ToCsv(rows, header: true));
                    }
                    sw.Stop();
                    Telemetry.Log("snapshot", Path.GetFileName(solution.FilePath ?? ""), rows.Count, sw.ElapsedMilliseconds);
                    return;
                }

                var (record, _, _) = await SnapshotAnalyzer.AnalyzeAsync(solution, cancellationToken);

                if (appendPath is not null)
                {
                    AppendCsv(record, appendPath);
                    Console.WriteLine($"appended to {appendPath}");
                }
                else if (format == OutputFormat.Json)
                {
                    WriteJson(record);
                }
                else
                {
                    WriteCompact(record);
                }

                sw.Stop();
                Telemetry.Log("snapshot", record.Solution, 1, sw.ElapsedMilliseconds);
            }
        });

        return command;
    }

    private static void WriteCompact(SnapshotRecord r)
    {
        var label = string.IsNullOrEmpty(r.Commit) ? r.Solution : $"{r.Solution} @ {r.Commit}";
        var dateLabel = string.IsNullOrEmpty(r.CommitDate) ? "unknown" : r.CommitDate[..10];
        Console.WriteLine($"reforge snapshot — {label} ({dateLabel})");
        Console.WriteLine("  size");
        Console.WriteLine($"    loc (prod)       {r.LocProd,10:N0}      files       {r.FilesProd,6:N0}");
        Console.WriteLine($"    loc (tests)      {r.LocTest,10:N0}      files       {r.FilesTest,6:N0}");
        Console.WriteLine($"    classes          {r.Classes,10:N0}      interfaces  {r.Interfaces,6:N0}");
        Console.WriteLine("  structure");
        Console.WriteLine($"    avg reach        {r.AvgReach,10:F1}      p95 {r.P95Reach}   max {r.MaxReach} ({r.MaxReachFile})");
        Console.WriteLine($"    core size        {r.CoreSizePct,10:P1}      {r.CoreFileCount} files in largest SCC");
        Console.WriteLine($"    cycle count      {r.CycleCount,10:N0}      non-trivial SCCs (size >= 2)");
        Console.WriteLine($"    avg fan-out      {r.AvgFanOut,10:F1}      max {r.MaxFanOut} ({r.MaxFanOutFile})");
        Console.WriteLine("  complexity");
        Console.WriteLine($"    avg cyclomatic   {r.AvgCyclomatic,10:F1}      p95 {r.P95Cyclomatic}   max {r.MaxCyclomatic} ({r.MaxCyclomaticMethod})");
        Console.WriteLine($"    avg class loc    {r.AvgClassLoc,10:F0}      p95 {r.P95ClassLoc}  max {r.MaxClassLoc} ({r.MaxClassLocName})");
    }

    private static void WriteJson(SnapshotRecord r)
    {
        var json = JsonSerializer.Serialize(r, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        });
        Console.WriteLine(json);
    }

    private static readonly string[] CsvHeader =
    [
        "commit_date", "commit", "solution",
        "loc_prod", "loc_test", "files_prod", "files_test",
        "classes", "interfaces",
        "avg_reach", "p95_reach", "max_reach", "max_reach_file",
        "core_size_pct", "core_file_count", "cycle_count",
        "avg_fanout", "max_fanout", "max_fanout_file",
        "avg_cyclomatic", "p95_cyclomatic", "max_cyclomatic", "max_cyclomatic_method",
        "avg_class_loc", "p95_class_loc", "max_class_loc", "max_class_loc_name"
    ];

    private static void AppendCsv(SnapshotRecord r, string path)
    {
        bool writeHeader = !File.Exists(path) || new FileInfo(path).Length == 0;
        var sb = new StringBuilder();
        if (writeHeader) sb.AppendLine(string.Join(",", CsvHeader));

        var row = new[]
        {
            r.CommitDate,
            r.Commit,
            r.Solution,
            r.LocProd.ToString(),
            r.LocTest.ToString(),
            r.FilesProd.ToString(),
            r.FilesTest.ToString(),
            r.Classes.ToString(),
            r.Interfaces.ToString(),
            r.AvgReach.ToString("F3"),
            r.P95Reach.ToString(),
            r.MaxReach.ToString(),
            CsvEscape(r.MaxReachFile),
            r.CoreSizePct.ToString("F6"),
            r.CoreFileCount.ToString(),
            r.CycleCount.ToString(),
            r.AvgFanOut.ToString("F3"),
            r.MaxFanOut.ToString(),
            CsvEscape(r.MaxFanOutFile),
            r.AvgCyclomatic.ToString("F3"),
            r.P95Cyclomatic.ToString(),
            r.MaxCyclomatic.ToString(),
            CsvEscape(r.MaxCyclomaticMethod),
            r.AvgClassLoc.ToString("F1"),
            r.P95ClassLoc.ToString(),
            r.MaxClassLoc.ToString(),
            CsvEscape(r.MaxClassLocName)
        };
        sb.AppendLine(string.Join(",", row));
        File.AppendAllText(path, sb.ToString());
    }

    internal static string CsvEscape(string value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        return value;
    }
}
