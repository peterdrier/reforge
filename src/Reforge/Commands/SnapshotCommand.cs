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

        var command = new Command("snapshot", "One-row macro-scale code health record for time-series charting")
        {
            appendOption
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var solutionPath = parseResult.GetValue(solutionOption);
            var format = parseResult.GetValue(formatOption);
            var appendPath = parseResult.GetValue(appendOption);
            var sw = Stopwatch.StartNew();

            var (solution, handle) = await WorkspaceHelper.OpenSolutionAsync(solutionPath);
            using (handle)
            {
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
        Console.WriteLine($"reforge snapshot — {label} ({r.Timestamp[..10]})");
        Console.WriteLine("  size");
        Console.WriteLine($"    loc (prod)       {r.LocProd,10:N0}      files       {r.FilesProd,6:N0}");
        Console.WriteLine($"    loc (tests)      {r.LocTest,10:N0}      files       {r.FilesTest,6:N0}");
        Console.WriteLine($"    classes          {r.Classes,10:N0}      interfaces  {r.Interfaces,6:N0}");
        Console.WriteLine("  structure");
        Console.WriteLine($"    propagation cost {r.PropagationCost,10:P1}      (files reachable from avg file)");
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
        "timestamp", "commit", "solution",
        "loc_prod", "loc_test", "files_prod", "files_test",
        "classes", "interfaces",
        "propagation_cost", "core_size_pct", "core_file_count", "cycle_count",
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
            r.Timestamp,
            r.Commit,
            r.Solution,
            r.LocProd.ToString(),
            r.LocTest.ToString(),
            r.FilesProd.ToString(),
            r.FilesTest.ToString(),
            r.Classes.ToString(),
            r.Interfaces.ToString(),
            r.PropagationCost.ToString("F6"),
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

    private static string CsvEscape(string value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        return value;
    }
}
