using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace Reforge.Tests;

/// <summary>
/// <c>projectReference</c> and <c>unusedProjectReference</c>: a section pays for every other
/// section its compilation uses, however the reference reaches it, and more for a csproj
/// reference it does not use.
/// </summary>
[Collection("SampleSolution")]
public class ProjectReferenceScoringTests
{
    private readonly SampleSolutionFixture _fixture;

    public ProjectReferenceScoringTests(SampleSolutionFixture fixture) => _fixture = fixture;

    private Task<ScoreReport> ScoreSampleAsync() =>
        new SurfaceScoreEngine(SurfaceScoreConfig.Default(), LocationHelper.GetSolutionDirectory(_fixture.Solution))
            .ScoreAsync(_fixture.Solution, CancellationToken.None);

    [Fact]
    public async Task SampleSolution_UnusedReference_IsChargedAtItsCsprojLine()
    {
        var report = await ScoreSampleAsync();

        var entry = Assert.Single(report.Groups["Tent"].Entries, e => e.Rule == "unusedProjectReference");
        Assert.Equal("SampleSolution.Core", entry.Symbol);
        Assert.Equal(25, entry.Points);
        Assert.EndsWith("SampleSolution.Tent.csproj", entry.File);
        Assert.Equal(11, entry.Line);
        Assert.Contains(report.Diagnostics, d => d.Code == "unusedProjectReference"
            && d.Message.Contains("SampleSolution.Tent.csproj:11 -> SampleSolution.Core", StringComparison.Ordinal));

        // Every other declared reference in the production projects is used.
        Assert.Single(report.Groups.Values.SelectMany(g => g.Entries), e => e.Rule == "unusedProjectReference");
    }

    [Fact]
    public async Task SampleSolution_UsedReferences_PayTheFloorOnlyWhereNothingElsePricesTheEdge()
    {
        var report = await ScoreSampleAsync();
        var charged = report.Groups.Values.SelectMany(g => g.Entries).Where(e => e.Rule == "projectReference").ToList();

        // Web reaches Services only for types no constructor-dependency rule prices, so it pays the
        // floor, at the csproj line that declares it. Web -> Core and Reporting -> Camp already pay
        // crossSection* charges, and Camp -> Camp.Contracts stays inside one section.
        var entry = Assert.Single(charged);
        Assert.Equal(("Web", "Services", 5, 11), (entry.Group, entry.Symbol, entry.Points, entry.Line));
        Assert.EndsWith("SampleSolution.Web.csproj", entry.File);
    }

    [Fact]
    public async Task DroppingAUsedDirectReferenceForATransitiveOne_EarnsNothing()
    {
        using var dir = new TempDir();
        var direct = await ScoreAdhocAsync(dir, topDeclares: new[] { "Mid", "Core" }, topReferences: new[] { "Mid", "Core" });
        var transitive = await ScoreAdhocAsync(dir, topDeclares: new[] { "Mid" }, topReferences: new[] { "Mid", "Core" });

        Assert.Equal(direct.Total, transitive.Total);
        var top = transitive.Groups["Top"].Entries.Where(e => e.Rule == "projectReference").ToList();
        Assert.Equal(new[] { "Core", "Mid" }, top.Select(e => e.Symbol).OrderBy(s => s, StringComparer.Ordinal));
        Assert.Contains(top, e => e.Detail == "Demo.Top -> Core (transitive)");
    }

    [Fact]
    public async Task UnusedReference_CostsMoreThanAUsedOne_AndTestProjectsAreNotCharged()
    {
        using var dir = new TempDir();
        var clean = await ScoreAdhocAsync(dir, topDeclares: new[] { "Mid", "Core" }, topReferences: new[] { "Mid", "Core" });
        var withUnused = await ScoreAdhocAsync(dir, topDeclares: new[] { "Mid", "Core", "Spare" },
            topReferences: new[] { "Mid", "Core", "Spare" });

        Assert.Equal(25, withUnused.Total - clean.Total);
        var entry = Assert.Single(withUnused.Groups["Top"].Entries, e => e.Rule == "unusedProjectReference");
        Assert.Equal("Demo.Spare", entry.Symbol);
        Assert.Equal("Demo.Top.csproj", entry.File);
        Assert.Equal(5, entry.Line); // the third <ProjectReference>, after the opening lines

        // Demo.Tests declares Spare and never uses it; test projects are outside the score.
        Assert.DoesNotContain(withUnused.Groups.Values.SelectMany(g => g.Entries),
            e => e.Detail?.StartsWith("Demo.Tests", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task AReferenceOnlyAViewNames_IsUsed()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(Path.Combine(dir.Path, "Views"));
        File.WriteAllText(Path.Combine(dir.Path, "Views", "Index.cshtml"), "@using Demo.Spare\n<p>@Model</p>");
        var report = await ScoreAdhocAsync(dir, topDeclares: new[] { "Mid", "Core", "Spare" },
            topReferences: new[] { "Mid", "Core", "Spare" });

        Assert.DoesNotContain(report.Groups.Values.SelectMany(g => g.Entries), e => e.Rule == "unusedProjectReference");
        Assert.Contains(report.Groups["Top"].Entries, e => e.Rule == "projectReference" && e.Symbol == "Spare");
    }

    [Fact]
    public async Task AnApplicationsReferences_AreNotJudgedUnused()
    {
        // A host that discovers its modules at run time references them only to ship them.
        using var dir = new TempDir();
        var report = await ScoreAdhocAsync(dir, topDeclares: new[] { "Mid", "Core", "Spare" },
            topReferences: new[] { "Mid", "Core", "Spare" }, topKind: OutputKind.ConsoleApplication);

        Assert.DoesNotContain(report.Groups.Values.SelectMany(g => g.Entries), e => e.Rule == "unusedProjectReference");
    }

    // ---------------- Ad-hoc solution with csproj files on disk ----------------

    private static readonly Lazy<MetadataReference[]> RuntimeReferences = new(() =>
        (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Where(p => p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && File.Exists(p))
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .ToArray());

    private static readonly Dictionary<string, string> Sources = new(StringComparer.Ordinal)
    {
        ["Core"] = "namespace Demo.Core; public class Foo { public int Value { get; set; } }",
        ["Mid"] = "namespace Demo.Mid; public class Bar { public Demo.Core.Foo Make() => new(); }",
        ["Top"] = "namespace Demo.Top; public class Baz { public int Use() => new Demo.Mid.Bar().Make().Value; public static void Main() { } }",
        ["Spare"] = "namespace Demo.Spare; public class Spare { public int Count { get; set; } }",
        ["Tests"] = "namespace Demo.Tests; public class BazTests { public int Run() => new Demo.Top.Baz().Use(); }",
    };

    /// <summary>
    /// Demo.Core, Demo.Mid (uses Core), Demo.Top (uses Mid and Core), Demo.Spare (used by nobody)
    /// and Demo.Tests (uses Top, declares Spare). <paramref name="topReferences"/> is what the
    /// workspace hands Top's compilation, as MSBuild would after flowing transitive references;
    /// <paramref name="topDeclares"/> is what Top's csproj says.
    /// </summary>
    private static async Task<ScoreReport> ScoreAdhocAsync(TempDir dir, string[] topDeclares, string[] topReferences,
        OutputKind topKind = OutputKind.DynamicallyLinkedLibrary)
    {
        var ids = Sources.Keys.ToDictionary(k => k, k => ProjectId.CreateNewId("Demo." + k), StringComparer.Ordinal);
        var refs = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["Core"] = Array.Empty<string>(),
            ["Mid"] = new[] { "Core" },
            ["Top"] = topReferences,
            ["Spare"] = Array.Empty<string>(),
            ["Tests"] = new[] { "Top", "Mid", "Core", "Spare" },
        };
        var declares = new Dictionary<string, string[]>(refs, StringComparer.Ordinal) { ["Top"] = topDeclares };

        var projects = new List<ProjectInfo>();
        foreach (var (name, source) in Sources)
        {
            var assembly = "Demo." + name;
            var csproj = Path.Combine(dir.Path, assembly + ".csproj");
            File.WriteAllText(csproj, string.Join("\n", new[] { "<Project Sdk=\"Microsoft.NET.Sdk\">", "  <ItemGroup>" }
                .Concat(declares[name].Select(d => $"    <ProjectReference Include=\"Demo.{d}.csproj\" />"))
                .Concat(new[] { "  </ItemGroup>", "</Project>" })));
            var file = Path.Combine(dir.Path, name, assembly + ".cs");
            var docId = DocumentId.CreateNewId(ids[name]);
            projects.Add(ProjectInfo.Create(ids[name], VersionStamp.Create(), assembly, assembly, LanguageNames.CSharp,
                filePath: csproj,
                compilationOptions: new CSharpCompilationOptions(name == "Top" ? topKind : OutputKind.DynamicallyLinkedLibrary),
                documents: new[]
                {
                    DocumentInfo.Create(docId, Path.GetFileName(file), filePath: file,
                        loader: TextLoader.From(TextAndVersion.Create(SourceText.From(source), VersionStamp.Create(), file)))
                },
                projectReferences: refs[name].Select(r => new ProjectReference(ids[r])),
                metadataReferences: RuntimeReferences.Value));
        }

        using var workspace = new AdhocWorkspace();
        var solution = workspace.AddSolution(SolutionInfo.Create(SolutionId.CreateNewId(), VersionStamp.Create(),
            filePath: Path.Combine(dir.Path, "Demo.slnx"), projects: projects));
        return await new SurfaceScoreEngine(SurfaceScoreConfig.Default(), dir.Path).ScoreAsync(solution, CancellationToken.None);
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("reforge-projref-").FullName;
        public void Dispose() { try { Directory.Delete(Path, recursive: true); } catch (IOException) { } }
    }
}
