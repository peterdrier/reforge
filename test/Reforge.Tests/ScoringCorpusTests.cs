using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Xunit.Abstractions;

namespace Reforge.Tests;

/// <summary>
/// Scores every before/after pair in <c>test/ScoringCorpus</c> and checks the direction of the
/// change against the pair's label in <c>manifest.json</c>: <c>better</c> must lower the total,
/// <c>worse</c> must raise it, <c>lateral</c> must leave it exactly equal. Totals are integers and a
/// lateral move is the same logic rearranged, so any difference at all is a finding.
///
/// <para>A pair whose manifest entry names a <c>gap</c> is expected to fail today. The test fails
/// when a gap pair starts passing (drop the marker) or an unmarked pair starts failing, so the
/// corpus works as a ratchet in CI while the gaps stay visible as data.</para>
///
/// <para>Each variant directory is one project; a subdirectory is a satellite project in its own
/// section, referenced by the main one. Set <c>REFORGE_CORPUS_REPORT</c> to a path to write the
/// results table, and <c>REFORGE_CORPUS_CONFIG</c> to score under another config (the ratchet is
/// skipped then, since the gap markers describe the default weights).</para>
/// </summary>
public class ScoringCorpusTests
{
    private const string MainAssembly = "Corpus.Main";
    private readonly ITestOutputHelper _output;

    public ScoringCorpusTests(ITestOutputHelper output) => _output = output;

    private sealed record Pair(string Id, string Label, string Rationale, string? Gap, string? Confidence);

    private sealed record Result(Pair Pair, ScoreReport Before, ScoreReport After)
    {
        public int Delta => After.Total - Before.Total;
        public bool Passes => Pair.Label switch
        {
            "better" => Delta < 0,
            "worse" => Delta > 0,
            "lateral" => Delta == 0,
            _ => throw new InvalidOperationException($"{Pair.Id}: unknown label '{Pair.Label}'"),
        };
    }

    [Fact]
    public async Task EveryPair_MovesTheScoreTheWayItsLabelSays()
    {
        var root = Path.Combine(SampleSolutionFixture.FindRepoRoot(AppContext.BaseDirectory), "test", "ScoringCorpus");
        var configPath = Environment.GetEnvironmentVariable("REFORGE_CORPUS_CONFIG");
        var config = string.IsNullOrEmpty(configPath)
            ? SurfaceScoreConfig.Default()
            : SurfaceScoreConfig.LoadOrDefault(configPath, root, out _);

        var results = new List<Result>();
        foreach (var pair in LoadManifest(Path.Combine(root, "manifest.json")))
        {
            var dir = Path.Combine(root, "Pairs", pair.Id);
            results.Add(new Result(pair,
                await ScoreAsync(Path.Combine(dir, "before"), root, config),
                await ScoreAsync(Path.Combine(dir, "after"), root, config)));
        }

        var table = Table(results) + Entries(results, Environment.GetEnvironmentVariable("REFORGE_CORPUS_ENTRIES"));
        _output.WriteLine(table);
        if (Environment.GetEnvironmentVariable("REFORGE_CORPUS_REPORT") is { Length: > 0 } reportPath)
            await File.WriteAllTextAsync(reportPath, table);

        if (!string.IsNullOrEmpty(configPath)) return;
        var surprises = results
            .Where(r => r.Passes == (r.Pair.Gap is not null))
            .Select(r => r.Passes
                ? $"{r.Pair.Id}: passes now; remove gap '{r.Pair.Gap}' from the manifest"
                : $"{r.Pair.Id}: {r.Pair.Label} expected, total {r.Before.Total} -> {r.After.Total}")
            .ToList();
        Assert.True(surprises.Count == 0, string.Join(Environment.NewLine, surprises));
    }

    private static List<Pair> LoadManifest(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.GetProperty("pairs").EnumerateArray().Select(p => new Pair(
            p.GetProperty("id").GetString()!,
            p.GetProperty("label").GetString()!,
            p.GetProperty("rationale").GetString()!,
            p.TryGetProperty("gap", out var g) ? g.GetString() : null,
            p.TryGetProperty("confidence", out var c) ? c.GetString() : null)).ToList();
    }

    private static string Table(List<Result> results)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{results.Count(r => r.Passes)} PASS, {results.Count(r => !r.Passes)} FAIL of {results.Count}");
        sb.AppendLine();
        sb.AppendLine("| pair | label | before | after | delta | verdict | gap | rules moved |");
        sb.AppendLine("|---|---|---:|---:|---:|---|---|---|");
        foreach (var r in results)
            sb.AppendLine($"| {r.Pair.Id} | {r.Pair.Label} | {r.Before.Total} | {r.After.Total} | {r.Delta:+0;-0;0} "
                + $"| {(r.Passes ? "PASS" : "FAIL")} | {r.Pair.Gap ?? ""} | {RulesMoved(r.Before, r.After)} |");
        return sb.ToString();
    }

    /// <summary>Every scored entry of the pairs named in <paramref name="ids"/> (comma separated), for diagnosing one pair.</summary>
    private static string Entries(List<Result> results, string? ids)
    {
        if (string.IsNullOrEmpty(ids)) return "";
        var wanted = ids.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var sb = new StringBuilder();
        foreach (var r in results.Where(r => wanted.Contains(r.Pair.Id)))
            foreach (var (side, report) in new[] { ("before", r.Before), ("after", r.After) })
            {
                sb.AppendLine().AppendLine($"{r.Pair.Id} {side}:");
                foreach (var e in report.Groups.Values.SelectMany(g => g.Entries))
                    sb.AppendLine($"  {e.Rule} {e.Points} {e.Symbol} {e.Detail}");
            }
        return sb.ToString();
    }

    private static string RulesMoved(ScoreReport before, ScoreReport after)
        => string.Join(", ", before.ByRule.Keys.Union(after.ByRule.Keys, StringComparer.OrdinalIgnoreCase)
            .Select(k => (Rule: k, Delta: after.ByRule.GetValueOrDefault(k) - before.ByRule.GetValueOrDefault(k)))
            .Where(x => x.Delta != 0)
            .OrderBy(x => x.Rule, StringComparer.Ordinal)
            .Select(x => $"{x.Rule} {x.Delta:+0;-0}"));

    private static readonly Lazy<MetadataReference[]> RuntimeReferences = new(() =>
        (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Where(p => p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && File.Exists(p))
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .ToArray());

    private const string ImplicitUsings = """
        global using global::System;
        global using global::System.Collections.Generic;
        global using global::System.IO;
        global using global::System.Linq;
        global using global::System.Net.Http;
        global using global::System.Threading;
        global using global::System.Threading.Tasks;
        """;

    private static async Task<ScoreReport> ScoreAsync(string variantDir, string solutionDir, SurfaceScoreConfig config)
    {
        Assert.True(Directory.Exists(variantDir), $"missing variant directory {variantDir}");
        var satellites = Directory.EnumerateDirectories(variantDir).OrderBy(d => d, StringComparer.Ordinal)
            .Select(d => Project("Corpus." + Path.GetFileName(d), d, Array.Empty<ProjectReference>()))
            .ToList();
        var main = Project(MainAssembly, variantDir, satellites.Select(s => new ProjectReference(s.Id)).ToList());

        using var workspace = new AdhocWorkspace();
        var solution = workspace.AddSolution(SolutionInfo.Create(SolutionId.CreateNewId(), VersionStamp.Create(),
            filePath: Path.Combine(solutionDir, "ScoringCorpus.slnx"), projects: satellites.Append(main).ToList()));

        foreach (var project in solution.Projects)
        {
            var errors = (await project.GetCompilationAsync())!.GetDiagnostics()
                .Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
            Assert.True(errors.Count == 0, $"{variantDir} does not compile: {string.Join("; ", errors.Take(3))}");
        }

        return await new SurfaceScoreEngine(config, solutionDir).ScoreAsync(solution, CancellationToken.None);
    }

    private static ProjectInfo Project(string assemblyName, string directory, IReadOnlyList<ProjectReference> references)
    {
        var id = ProjectId.CreateNewId(assemblyName);
        var documents = Directory.EnumerateFiles(directory, "*.cs").OrderBy(f => f, StringComparer.Ordinal)
            .Select(f => Document(id, f, File.ReadAllText(f)))
            .Append(Document(id, Path.Combine(directory, assemblyName + ".ImplicitUsings.g.cs"), ImplicitUsings))
            .ToList();
        return ProjectInfo.Create(id, VersionStamp.Create(), assemblyName, assemblyName, LanguageNames.CSharp,
            filePath: Path.Combine(directory, assemblyName + ".csproj"),
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable),
            documents: documents, projectReferences: references, metadataReferences: RuntimeReferences.Value);
    }

    private static DocumentInfo Document(ProjectId projectId, string path, string text)
        => DocumentInfo.Create(DocumentId.CreateNewId(projectId), Path.GetFileName(path),
            loader: TextLoader.From(TextAndVersion.Create(SourceText.From(text, Encoding.UTF8), VersionStamp.Create(), path)),
            filePath: path);
}
