using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using Microsoft.CodeAnalysis;

namespace Reforge;

/// <summary>
/// One <c>snapshot --by-assembly</c> row: a section's size, complexity, surface census, tokens,
/// score and test value at one commit. The section named <c>(solution)</c> is the pooled rollup.
/// </summary>
/// <remarks>
/// Columns are the per-section blocks <c>surface-score</c> reports, flattened so a history run can
/// append one file per commit and chart any column per section without parsing a score report.
/// <c>ReforgeVersion</c> and <c>ConfigHash</c> tell rows scored under different rules apart. The
/// main score columns are filled by a full scoring pass unless the run passed <c>--no-score</c>,
/// which leaves them null; the size charges and the test score are priced off the token census either way.
/// </remarks>
public sealed record SectionSnapshotRow(
    string CommitDate,
    string Commit,
    string Solution,
    string Section,
    string ReforgeVersion,
    string ConfigHash,
    int LoadFailures,
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
    int TokensProd,
    string TokensProdSource,
    int TokensContracts,
    string TokensContractsSource,
    int? ScoreSurface,
    int? ScoreImplementation,
    int? ScoreTotal,
    int SectionSizeCharge,
    int ClassSizeCharge,
    int TestScore,
    int TestLoc,
    int TestFiles,
    int TestMethods,
    double? TestMethodsPerClass,
    int TokensTests,
    string TokensTestsSource,
    double? TestTokenRatio,
    /// <summary>Null when no coverage report was given or it said nothing about the section.</summary>
    int? CoverageCovered,
    int? CoverageTotal,
    int? CoveragePercent);

public static class SectionSnapshotAnalyzer
{
    public const string SolutionRow = "(solution)";

    /// <summary>
    /// One row per section plus the rollup. With <paramref name="score"/> (the default) the full
    /// engine runs and every column comes off its report; without, only the passes the columns other
    /// than the main score need, about half the cost.
    /// </summary>
    public static async Task<List<SectionSnapshotRow>> AnalyzeAsync(Solution solution, string? coveragePath, CancellationToken ct,
        bool score = true, TokenMeasurer? tokens = null, SurfaceScoreConfig? config = null)
    {
        var dir = LocationHelper.GetSolutionDirectory(solution);
        config ??= SurfaceScoreConfig.LoadOrDefault(null, dir, out _);
        var report = score
            ? await new SurfaceScoreEngine(config, dir) { Tokens = tokens }.ScoreAsync(solution, ct)
            : await CensusAsync(solution, config, dir, tokens, ct);

        if (coveragePath is not null)
        {
            var ingest = CoverageReportReader.Read(coveragePath, report.SectionByAssembly);
            report.AttachCoverage(ingest);
            if (ingest.UnmatchedAssemblies.Count > 0)
                Console.Error.WriteLine($"coverage: assemblies not in the solution, left out: {string.Join(", ", ingest.UnmatchedAssemblies)}");
        }

        var stamp = new Stamp(
            SnapshotAnalyzer.TryGetGitCommitDate(solution),
            SnapshotAnalyzer.TryGetGitCommit(solution, full: true),
            Path.GetFileName(solution.FilePath ?? ""),
            ReforgeVersion(),
            ConfigHash(config),
            WorkspaceHelper.LoadFailures);

        // One row per section of the corpus, not per section with metrics: a section whose every
        // declaration is generated has no metrics entry but is still a section with a census.
        var rows = new List<SectionSnapshotRow>();
        foreach (var section in report.ConfiguredSections.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
        {
            var group = report.Groups.GetValueOrDefault(section);
            var tokensOf = report.Tokens?.BySection.GetValueOrDefault(section);
            rows.Add(Row(stamp, section,
                report.MetricsBySection.GetValueOrDefault(section) ?? SectionMetrics.Empty,
                report.SurfaceBySection.GetValueOrDefault(section) ?? SurfaceCensus.Empty,
                tokensOf?.Main ?? default, tokensOf?.Contracts ?? default, tokensOf?.Tests ?? default,
                score ? (group?.SurfaceTotal ?? 0, group?.ImplementationShapeTotal ?? 0, group?.Total ?? 0) : null,
                tokensOf is null ? default : SizePricing.ForSection(tokensOf, config),
                report.TestValueOf(section),
                report.CoverageBySection.GetValueOrDefault(section) ?? SectionCoverage.Empty));
        }

        var all = report.Tokens?.BySection.Values.ToList() ?? [];
        var charges = all.Select(t => SizePricing.ForSection(t, config)).ToList();
        rows.Add(Row(stamp, SolutionRow, report.Metrics, report.Surface,
            TokenFigure.Sum(all.Select(t => t.Main)), TokenFigure.Sum(all.Select(t => t.Contracts)), TokenFigure.Sum(all.Select(t => t.Tests)),
            score ? (report.SurfaceTotal, report.ImplementationShapeTotal, report.Total) : null,
            new SizeCharges(charges.Sum(c => c.Section), charges.Sum(c => c.Classes), charges.Sum(c => c.Tests)),
            report.TestValueOf(null), report.Coverage));
        return rows;
    }

    /// <summary>
    /// The report blocks the columns read, without the scoring passes: metrics, surface census,
    /// test mass and tokens, with the test score priced off the tokens as the engine prices it.
    /// </summary>
    private static async Task<ScoreReport> CensusAsync(Solution solution, SurfaceScoreConfig config, string dir, TokenMeasurer? tokens, CancellationToken ct)
    {
        var classified = await SolutionClassifier.ClassifyAsync(solution, config, dir, ct);
        var report = new ScoreReport();
        report.ConfiguredSections.AddRange(classified.Select(c => c.Group).Distinct(StringComparer.OrdinalIgnoreCase));
        foreach (var c in classified)
            if (c.Type.ContainingAssembly?.Name is { } assembly) report.SectionByAssembly[assembly] = c.Group;

        var (metricsBySection, solutionMetrics) = SectionMetricsAnalyzer.Analyze(classified, ct);
        report.Metrics = solutionMetrics;
        foreach (var (section, m) in metricsBySection) report.MetricsBySection[section] = m;

        var behaviour = await MethodBehaviourAnalyzer.AnalyzeAsync(solution, classified, ct);
        var census = SurfaceCensusAnalyzer.Analyze(classified, behaviour);
        report.Surface = census.Solution;
        foreach (var (section, s) in census.BySection) report.SurfaceBySection[section] = s;

        var (testsBySection, solutionTests, _) = await TestMassAnalyzer.AnalyzeAsync(solution, classified, metricsBySection, ct);
        report.Tests = solutionTests;
        foreach (var (section, t) in testsBySection) report.TestsBySection[section] = t;

        var testFiles = await TestMassAnalyzer.CollectFilesAsync(solution, classified, ct);
        report.Tokens = await TokenCensus.MeasureAsync(TokenCensus.Collect(classified, ct), tokens, ct, testFiles);
        foreach (var (section, t) in report.Tokens.BySection)
        {
            var test = SizePricing.ForSection(t, config).Tests;
            report.TestScoreBySection[section] = test;
            report.TestTotal += test;
        }
        return report;
    }

    private sealed record Stamp(string CommitDate, string Commit, string Solution, string Version, string ConfigHash, int LoadFailures);

    private static SectionSnapshotRow Row(
        Stamp st, string section, SectionMetrics m, SurfaceCensus s,
        TokenFigure prod, TokenFigure contracts, TokenFigure testTokens,
        (int Surface, int Implementation, int Total)? score, SizeCharges charges, TestValue test, SectionCoverage cov) => new(
        st.CommitDate, st.Commit, st.Solution, section, st.Version, st.ConfigHash, st.LoadFailures,
        m.LocProd, m.Files, m.Classes, m.Interfaces, m.Methods,
        m.Cognitive.Avg, m.Cognitive.P95, m.Cognitive.Max,
        m.Cyclomatic.Avg, m.Cyclomatic.P95, m.Cyclomatic.Max,
        m.MaxClassLoc, m.MaxClassLocName,
        s.ExportedTypes, s.InternalTypes,
        s.ExportedReadMethods, s.ExportedWriteMethods, s.InternalReadMethods, s.InternalWriteMethods,
        s.ExportedTypesUnusedExternally, s.ExportedMethodsUnusedExternally, s.ExportedWriteMethodsUnusedExternally,
        prod.Value, Source(prod), contracts.Value, Source(contracts),
        score?.Surface, score?.Implementation, score?.Total,
        charges.Section, charges.Classes,
        test.Score, test.Mass.Loc, test.Mass.Files, test.Mass.Methods, test.MethodsPerClass,
        testTokens.Value, Source(testTokens), test.TokenRatio,
        cov.HasData ? cov.Covered : null, cov.HasData ? cov.Total : null, cov.HasData ? cov.Percent : null);

    /// <summary><c>exact</c>, <c>estimate</c> or <c>mixed</c>; empty for a figure with nothing in it.</summary>
    private static string Source(TokenFigure f) => f.Value == 0 ? "" : f.Provenance;

    /// <summary>The package version, without the source-revision suffix.</summary>
    internal static string ReforgeVersion()
    {
        var version = typeof(SectionSnapshotAnalyzer).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
        var plus = version.IndexOf('+');
        return plus < 0 ? version : version[..plus];
    }

    /// <summary>
    /// The first 8 hex digits of a SHA-256 over the effective config (file merged with defaults), so
    /// rows priced under different weights or classifications can be told apart.
    /// </summary>
    internal static string ConfigHash(SurfaceScoreConfig config)
    {
        var json = JsonSerializer.Serialize(config);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)))[..8];
    }

    /// <summary>
    /// The column set. Any change to it is a new schema: <see cref="AppendCsv"/> refuses a file
    /// whose header differs, so a history file never mixes row widths.
    /// </summary>
    public static readonly string[] CsvHeader =
    [
        "commit_date", "commit", "solution", "section",
        "reforge_version", "config_hash", "load_failures",
        "loc_prod", "files", "classes", "interfaces", "methods",
        "cognitive_avg", "cognitive_p95", "cognitive_max",
        "cyclomatic_avg", "cyclomatic_p95", "cyclomatic_max",
        "max_class_loc", "max_class_loc_name",
        "exported_types", "internal_types",
        "exported_read_methods", "exported_write_methods", "internal_read_methods", "internal_write_methods",
        "exported_types_unused_externally", "exported_methods_unused_externally", "exported_write_methods_unused_externally",
        "tokens_prod", "tokens_prod_source", "tokens_contracts", "tokens_contracts_source",
        "score_surface", "score_implementation", "score_total", "section_size_charge", "class_size_charge",
        "test_score", "test_loc", "test_files", "test_methods", "test_methods_per_class",
        "tokens_tests", "tokens_tests_source", "test_token_ratio",
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
                Commands.SnapshotCommand.CsvEscape(r.ReforgeVersion), r.ConfigHash, I(r.LoadFailures),
                I(r.LocProd), I(r.Files), I(r.Classes), I(r.Interfaces), I(r.Methods),
                F(r.CognitiveAvg), I(r.CognitiveP95), I(r.CognitiveMax),
                F(r.CyclomaticAvg), I(r.CyclomaticP95), I(r.CyclomaticMax),
                I(r.MaxClassLoc), Commands.SnapshotCommand.CsvEscape(r.MaxClassLocName),
                I(r.ExportedTypes), I(r.InternalTypes),
                I(r.ExportedReadMethods), I(r.ExportedWriteMethods), I(r.InternalReadMethods), I(r.InternalWriteMethods),
                I(r.ExportedTypesUnusedExternally), I(r.ExportedMethodsUnusedExternally), I(r.ExportedWriteMethodsUnusedExternally),
                I(r.TokensProd), r.TokensProdSource, I(r.TokensContracts), r.TokensContractsSource,
                I(r.ScoreSurface), I(r.ScoreImplementation), I(r.ScoreTotal), I(r.SectionSizeCharge), I(r.ClassSizeCharge),
                I(r.TestScore), I(r.TestLoc), I(r.TestFiles), I(r.TestMethods), F(r.TestMethodsPerClass, "F3"),
                I(r.TokensTests), r.TokensTestsSource, F(r.TestTokenRatio, "F3"),
                I(r.CoverageCovered), I(r.CoverageTotal), I(r.CoveragePercent)
            }));
        }
        return sb.ToString();
    }

    // Invariant: a comma decimal separator would split the cell. Null is an empty cell.
    private static string I(int? v) => v?.ToString(CultureInfo.InvariantCulture) ?? "";
    private static string F(double? v, string format = "F2") => v?.ToString(format, CultureInfo.InvariantCulture) ?? "";

    /// <summary>
    /// Appends rows to a history file, writing the header first when the file is new or empty.
    /// A file whose first line is not this version's header is refused rather than appended to:
    /// rows wider or narrower than the header would mislabel every later column, and the
    /// mismatch would not show until something charted it.
    /// </summary>
    public static void AppendCsv(IReadOnlyList<SectionSnapshotRow> rows, string path)
    {
        bool writeHeader = !File.Exists(path) || new FileInfo(path).Length == 0;
        if (!writeHeader)
        {
            var expected = string.Join(",", CsvHeader);
            using var reader = new StreamReader(path);
            var first = (reader.ReadLine() ?? "").TrimEnd('\r');
            if (!string.Equals(first, expected, StringComparison.Ordinal))
                throw new InvalidDataException(
                    $"{path}: header does not match this version's {CsvHeader.Length} columns " +
                    $"(file has {first.Split(',').Length}); start a new file for this schema rather than mixing row widths.");
        }
        File.AppendAllText(path, ToCsv(rows, writeHeader));
    }
}
