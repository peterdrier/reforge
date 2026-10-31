using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace Reforge.Tests;

/// <summary>
/// The size curves at their thresholds, and the engine pricing tokens: a fake counter's exact
/// counts where it has them, estimates where it doesn't.
/// </summary>
public class SizePricingTests
{
    private static readonly SurfaceScoreConfig Config = SurfaceScoreConfig.Default();

    [Theory]
    [InlineData(50_000, 0)]
    [InlineData(75_000, 50)]          // 2 per k above 50k
    [InlineData(100_000, 100)]
    [InlineData(200_000, 800)]        // 300 linear + 50% x 100^1.5
    public void Section_FreeThenLinearThenTaxed(int tokens, int points) =>
        Assert.Equal(points, SizePricing.Section(tokens, Config.Weight("sectionSize"), Config));

    [Theory]
    [InlineData(8_000, 0)]
    [InlineData(25_000, 85)]          // 5 per k above 8k
    [InlineData(41_000, 485)]         // 165 linear + 500% x 16^1.5
    public void Class_FreeThenLinearThenTaxed(int tokens, int points) =>
        Assert.Equal(points, SizePricing.Class(tokens, Config.Weight("classSize"), Config));

    [Theory]
    [InlineData(90_000, 100_000, 0)]
    [InlineData(150_000, 100_000, 50)]  // 1 per k of test tokens above the section's own
    [InlineData(403_000, 260_000, 143)] // Humans Shifts
    public void Tests_LinearAboveTheSectionsOwnSize(int tests, int prod, int points) =>
        Assert.Equal(points, SizePricing.Tests(tests, prod, Config.Weight("testSize"), Config));

    [Fact]
    public void ZeroWeight_TurnsOffTheTaxToo() =>
        Assert.Equal(0, SizePricing.Section(500_000, 0, Config));

    [Fact]
    public void SplittingAClassIntoSiblings_LowersItsCharge()
    {
        var whole = SizePricing.Class(60_000, 5, Config);
        var halves = 2 * SizePricing.Class(30_000, 5, Config);
        Assert.True(halves < whole, $"{halves} vs {whole}");
    }

    [Fact]
    public void Collect_LeavesNestedTypesInsideTheirContainer()
    {
        const string source = "namespace N;\npublic class Outer { private sealed class Inner { } }\n";
        var compilation = CSharpCompilation.Create("Asm", [CSharpSyntaxTree.ParseText(source, path: "/src/Outer.cs")],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]);
        var outer = compilation.GetTypeByMetadataName("N.Outer")!;
        var inner = compilation.GetTypeByMetadataName("N.Outer+Inner")!;
        var classified = new[] { outer, inner }.Select(t => new ClassifiedType(t, "Sec", [], "/src/Outer.cs", t.Locations[0]));

        var texts = TokenCensus.Collect(classified, CancellationToken.None);

        Assert.True(texts.Single(t => t.Name == "Outer").IsClass);
        Assert.False(texts.Single(t => t.Name != "Outer").IsClass);
    }

    // ---------------- Engine ----------------

    private const string Prod = "namespace App.Core;\npublic sealed class Ledger { public int Balance(int a) => a + 1; }\n";
    private const string Tests = "namespace App.Core.Tests;\npublic sealed class LedgerTests { public void Adds() { if (new App.Core.Ledger().Balance(1) != 2) throw new System.Exception(); } }\n";

    /// <summary>1,000 tokens per letter or digit; "." nets to zero.</summary>
    private sealed class FakeCounter(Func<string, bool>? fails = null) : ITokenCounter
    {
        public string Model => "fake";
        public Task<int?> CountAsync(string text, CancellationToken ct) =>
            Task.FromResult(fails?.Invoke(text) == true ? null : (int?)(1000 * text.Count(char.IsLetterOrDigit) + 3));
    }

    [Fact]
    public async Task WithTokens_ClassAndTestsArePriced()
    {
        var report = await ScoreAsync(new TokenMeasurer(new FakeCounter(), TokenCache.InMemory()));

        var section = Assert.Single(report.Tokens!.BySection.Values);
        Assert.True(section.Tests.Value > section.Total.Value, $"tests {section.Tests} vs prod {section.Total}");
        Assert.False(section.Total.Estimated || section.Tests.Estimated);
        Assert.Equal(1, section.TestFiles);
        Assert.Equal(SizePricing.Class(section.Classes.Single().Tokens.Value, 5, Config), report.ByRule["classSize"]);
        Assert.True(report.ByRule["classSize"] > 0);
        Assert.Equal(report.ByRule["classSize"] + report.ByRule.GetValueOrDefault("sectionSize"),
            report.ImplementationShapeTotal - ImplementationShapeWithout(report));

        // testSize is the test score: priced the same way, kept out of every main total.
        var testScore = SizePricing.Tests(section.Tests.Value, section.Total.Value, 1, Config);
        Assert.True(testScore > 0);
        Assert.Equal(testScore, report.TestTotal);
        Assert.Equal(testScore, report.TestValueOf(report.Tokens.BySection.Keys.Single()).Score);
        Assert.False(report.ByRule.ContainsKey("testSize"));
        Assert.Equal(report.SurfaceTotal + report.ImplementationShapeTotal, report.Total);
    }

    /// <summary>A *Service class of about 1,020 nonblank lines, about 10k estimated tokens.</summary>
    private static readonly string BigService = "namespace App.Core;\npublic sealed class BigService\n{\n"
        + string.Concat(Enumerable.Range(0, 340).Select(i => $"    public int M{i}(int a)\n    {{ return a + {i}; }}\n\n    // {i}\n"))
        + "}\n";

    [Fact]
    public async Task WithoutCounts_SizeIsEstimated()
    {
        var report = await ScoreAsync(null, BigService);

        var section = Assert.Single(report.Tokens!.BySection.Values);
        Assert.True(section.Total.Estimated && section.Tests.Estimated && section.Classes.Single().Tokens.Estimated);
        Assert.Equal(TokenEstimator.Estimate(BigService), section.Total.Value);
        Assert.True(report.ByRule.GetValueOrDefault("classSize") > 0);
        Assert.False(report.ByRule.ContainsKey("largeClass"));
    }

    [Fact]
    public async Task FailedCounts_FallBackToEstimates_PerFile()
    {
        var report = await ScoreAsync(new TokenMeasurer(new FakeCounter(t => t.Contains("class Ledger {")), TokenCache.InMemory()));

        var section = Assert.Single(report.Tokens!.BySection.Values);
        Assert.Equal(new TokenFigure(TokenEstimator.Estimate(Prod), true), section.Total);
        Assert.False(section.Tests.Estimated, "the test file was counted, so its exact count wins");
        Assert.DoesNotContain(report.Diagnostics, d => d.Code == "size-unpriced");
    }

    [Fact]
    public async Task EstimateNearAThreshold_IsFlagged_InTheCensusAndTheEntry()
    {
        // The free allowance 3% under the class's estimate: inside the 5% band.
        var config = SurfaceScoreConfig.Default();
        config.ClassSizeFreeTokens = (int)(await ClassTokensAsync(null) * 0.97);

        var report = await ScoreAsync(null, BigService, config);

        var big = report.Tokens!.BySection.Values.Single().Classes.Single();
        Assert.True(big.Tokens.NearThreshold, big.Tokens.ToString());
        var entry = Assert.Single(report.Groups.Values.SelectMany(g => g.Entries), e => e.Rule == "classSize");
        Assert.Contains("near-threshold", entry.Detail);
    }

    [Fact]
    public async Task ExactCountAtAThreshold_IsNotFlagged()
    {
        var config = SurfaceScoreConfig.Default();
        config.ClassSizeFreeTokens = await ClassTokensAsync(new TokenMeasurer(new FakeCounter(), TokenCache.InMemory()));

        var report = await ScoreAsync(new TokenMeasurer(new FakeCounter(), TokenCache.InMemory()), BigService, config);

        var big = report.Tokens!.BySection.Values.Single().Classes.Single();
        Assert.False(big.Tokens.Estimated || big.Tokens.NearThreshold);
    }

    private static async Task<int> ClassTokensAsync(TokenMeasurer? tokens) =>
        (await ScoreAsync(tokens, BigService)).Tokens!.BySection.Values.Single().Classes.Single().Tokens.Value;

    private static int ImplementationShapeWithout(ScoreReport report) =>
        report.ByRule.Where(kv => SurfaceScoreRuleGroups.IsImplementationShape(kv.Key) && kv.Key is not ("sectionSize" or "classSize"))
            .Sum(kv => kv.Value);

    private static async Task<ScoreReport> ScoreAsync(TokenMeasurer? tokens, string prod = Prod, SurfaceScoreConfig? config = null)
    {
        var refs = (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Where(p => p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .ToList();
        var root = Path.Combine(Path.GetTempPath(), "reforge-size");
        var prodId = ProjectId.CreateNewId();
        var testId = ProjectId.CreateNewId();
        ProjectInfo Project(ProjectId id, string name, string source, IEnumerable<ProjectReference> references) =>
            ProjectInfo.Create(id, VersionStamp.Default, name, name, LanguageNames.CSharp,
                filePath: Path.Combine(root, name, name + ".csproj"),
                compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
                metadataReferences: refs, projectReferences: references,
                documents: [DocumentInfo.Create(DocumentId.CreateNewId(id), name + ".cs", filePath: Path.Combine(root, name, name + ".cs"),
                    loader: TextLoader.From(TextAndVersion.Create(SourceText.From(source, Encoding.UTF8), VersionStamp.Default)))]);

        using var workspace = new AdhocWorkspace();
        var solution = workspace.AddSolution(SolutionInfo.Create(SolutionId.CreateNewId(), VersionStamp.Default,
            filePath: Path.Combine(root, "App.slnx"),
            projects: [Project(prodId, "App.Core", prod, []), Project(testId, "App.Core.Tests", Tests, [new ProjectReference(prodId)])]));

        return await new SurfaceScoreEngine(config ?? Config, root) { Tokens = tokens }.ScoreAsync(solution, CancellationToken.None);
    }
}
