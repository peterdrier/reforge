using System.Text;
using Microsoft.CodeAnalysis;

namespace Reforge;

/// <summary>
/// One <c>snapshot --by-assembly</c> row: a section's size, complexity, surface census and test
/// mass at one commit. The section named <c>(solution)</c> is the pooled rollup.
/// </summary>
/// <remarks>
/// Columns are the per-section blocks <c>surface-score</c> already reports (<see cref="SectionMetrics"/>,
/// <see cref="SurfaceCensus"/>, <see cref="TestMass"/>), flattened so a history run can append one
/// file per commit and chart any column per section without parsing a score report. No score column:
/// the score moves when the rules change, and a size series should not.
/// </remarks>
public sealed record SectionSnapshotRow(
    string CommitDate,
    string Commit,
    string Solution,
    string Section,
    int LocProd,
    int Files,
    int Classes,
    int Interfaces,
    int Methods,
    double CognitiveAvg,
    int CognitiveP95,
    int CognitiveMax,
    double CyclomaticAvg,
    int CyclomaticP95,
    int CyclomaticMax,
    int MaxClassLoc,
    string MaxClassLocName,
    int ExportedTypes,
    int InternalTypes,
    int ExportedReadMethods,
    int ExportedWriteMethods,
    int InternalReadMethods,
    int InternalWriteMethods,
    int ExportedTypesUnusedExternally,
    int ExportedMethodsUnusedExternally,
    int ExportedWriteMethodsUnusedExternally,
    int TestLoc,
    int TestFiles,
    /// <summary>Null when no coverage report was given or it said nothing about the section.</summary>
    int? CoverageCovered,
    int? CoverageTotal,
    int? CoveragePercent);

public static class SectionSnapshotAnalyzer
{
    public const string SolutionRow = "(solution)";

    public static async Task<List<SectionSnapshotRow>> AnalyzeAsync(Solution solution, string? coveragePath, CancellationToken ct)
    {
        var dir = LocationHelper.GetSolutionDirectory(solution);
        var config = SurfaceScoreConfig.LoadOrDefault(null, dir, out _);
        var classified = await SolutionClassifier.ClassifyAsync(solution, config, dir, ct);

        var (metricsBySection, solutionMetrics) = SectionMetricsAnalyzer.Analyze(classified, ct);
        var behaviour = await MethodBehaviourAnalyzer.AnalyzeAsync(solution, classified, ct);
        var census = SurfaceCensusAnalyzer.Analyze(classified, behaviour);
        var (testsBySection, solutionTests, _) = await TestMassAnalyzer.AnalyzeAsync(solution, classified, metricsBySection, ct);

        var coverageBySection = new Dictionary<string, SectionCoverage>(StringComparer.OrdinalIgnoreCase);
        var solutionCoverage = SectionCoverage.Empty;
        if (coveragePath is not null)
        {
            var sectionByAssembly = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var c in classified)
                if (c.Type.ContainingAssembly?.Name is { } assembly) sectionByAssembly[assembly] = c.Group;
            var ingest = CoverageReportReader.Read(coveragePath, sectionByAssembly);
            coverageBySection = ingest.BySection;
            solutionCoverage = ingest.Solution;
            if (ingest.UnmatchedAssemblies.Count > 0)
                Console.Error.WriteLine($"coverage: assemblies not in the solution, left out: {string.Join(", ", ingest.UnmatchedAssemblies)}");
        }

        var commitDate = SnapshotAnalyzer.TryGetGitCommitDate(solution);
        var commit = SnapshotAnalyzer.TryGetGitCommit(solution);
        var solutionName = Path.GetFileName(solution.FilePath ?? "");

        var rows = new List<SectionSnapshotRow>();
        foreach (var section in metricsBySection.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
        {
            rows.Add(Row(commitDate, commit, solutionName, section,
                metricsBySection[section],
                census.BySection.TryGetValue(section, out var c) ? c : SurfaceCensus.Empty,
                testsBySection.TryGetValue(section, out var t) ? t : TestMass.Empty,
                coverageBySection.TryGetValue(section, out var cov) ? cov : SectionCoverage.Empty));
        }
        rows.Add(Row(commitDate, commit, solutionName, SolutionRow, solutionMetrics, census.Solution, solutionTests, solutionCoverage));
        return rows;
    }

    private static SectionSnapshotRow Row(
        string commitDate, string commit, string solution, string section,
        SectionMetrics m, SurfaceCensus s, TestMass t, SectionCoverage cov) => new(
        commitDate, commit, solution, section,
        m.LocProd, m.Files, m.Classes, m.Interfaces, m.Methods,
        m.Cognitive.Avg, m.Cognitive.P95, m.Cognitive.Max,
        m.Cyclomatic.Avg, m.Cyclomatic.P95, m.Cyclomatic.Max,
        m.MaxClassLoc, m.MaxClassLocName,
        s.ExportedTypes, s.InternalTypes,
        s.ExportedReadMethods, s.ExportedWriteMethods, s.InternalReadMethods, s.InternalWriteMethods,
        s.ExportedTypesUnusedExternally, s.ExportedMethodsUnusedExternally, s.ExportedWriteMethodsUnusedExternally,
        t.Loc, t.Files,
        cov.HasData ? cov.Covered : null, cov.HasData ? cov.Total : null, cov.HasData ? cov.Percent : null);

    public static readonly string[] CsvHeader =
    [
        "commit_date", "commit", "solution", "section",
        "loc_prod", "files", "classes", "interfaces", "methods",
        "cognitive_avg", "cognitive_p95", "cognitive_max",
        "cyclomatic_avg", "cyclomatic_p95", "cyclomatic_max",
        "max_class_loc", "max_class_loc_name",
        "exported_types", "internal_types",
        "exported_read_methods", "exported_write_methods", "internal_read_methods", "internal_write_methods",
        "exported_types_unused_externally", "exported_methods_unused_externally", "exported_write_methods_unused_externally",
        "test_loc", "test_files",
        "coverage_covered", "coverage_total", "coverage_pct"
    ];

    public static string ToCsv(IReadOnlyList<SectionSnapshotRow> rows, bool header)
    {
        var sb = new StringBuilder();
        if (header) sb.AppendLine(string.Join(",", CsvHeader));
        foreach (var r in rows)
        {
            sb.AppendLine(string.Join(",", new[]
            {
                r.CommitDate, r.Commit, Commands.SnapshotCommand.CsvEscape(r.Solution), Commands.SnapshotCommand.CsvEscape(r.Section),
                r.LocProd.ToString(), r.Files.ToString(), r.Classes.ToString(), r.Interfaces.ToString(), r.Methods.ToString(),
                r.CognitiveAvg.ToString("F2"), r.CognitiveP95.ToString(), r.CognitiveMax.ToString(),
                r.CyclomaticAvg.ToString("F2"), r.CyclomaticP95.ToString(), r.CyclomaticMax.ToString(),
                r.MaxClassLoc.ToString(), Commands.SnapshotCommand.CsvEscape(r.MaxClassLocName),
                r.ExportedTypes.ToString(), r.InternalTypes.ToString(),
                r.ExportedReadMethods.ToString(), r.ExportedWriteMethods.ToString(),
                r.InternalReadMethods.ToString(), r.InternalWriteMethods.ToString(),
                r.ExportedTypesUnusedExternally.ToString(), r.ExportedMethodsUnusedExternally.ToString(),
                r.ExportedWriteMethodsUnusedExternally.ToString(),
                r.TestLoc.ToString(), r.TestFiles.ToString(),
                r.CoverageCovered?.ToString() ?? "", r.CoverageTotal?.ToString() ?? "", r.CoveragePercent?.ToString() ?? ""
            }));
        }
        return sb.ToString();
    }

    public static void AppendCsv(IReadOnlyList<SectionSnapshotRow> rows, string path)
    {
        bool writeHeader = !File.Exists(path) || new FileInfo(path).Length == 0;
        File.AppendAllText(path, ToCsv(rows, writeHeader));
    }
}
