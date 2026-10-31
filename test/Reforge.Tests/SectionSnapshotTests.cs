using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Reforge.Tests;

/// <summary>
/// The <c>snapshot --by-assembly</c> columns past the census: the stamp, tokens, the main score and
/// size charges, and the test value kept apart from the main score.
/// </summary>
[Collection("SampleSolution")]
public class SectionSnapshotTests
{
    private readonly SampleSolutionFixture _fixture;
    public SectionSnapshotTests(SampleSolutionFixture fixture) => _fixture = fixture;

    /// <summary>Default weights with thresholds low enough that the sample's sizes are charged.</summary>
    private static SurfaceScoreConfig LowThresholds()
    {
        var config = SurfaceScoreConfig.Default();
        config.SectionSizeFreeTokens = 100;
        config.ClassSizeFreeTokens = 50;
        config.TestSizeFreePercent = 1;
        config.Weights["testSize"] = 1000;
        return config;
    }

    private string Dir => LocationHelper.GetSolutionDirectory(_fixture.Solution);

    [Fact]
    public async Task Stamp_FullCommitVersionAndConfigHash()
    {
        var rows = await SectionSnapshotAnalyzer.AnalyzeAsync(_fixture.Solution, null, CancellationToken.None);

        Assert.All(rows, r => Assert.Matches("^[0-9a-f]{40}$", r.Commit));
        Assert.All(rows, r => Assert.Matches(@"^\d+\.\d+\.\d+", r.ReforgeVersion));
        Assert.DoesNotContain('+', rows[0].ReforgeVersion);
        Assert.Equal(0, rows[0].LoadFailures);

        var hash = SectionSnapshotAnalyzer.ConfigHash(SurfaceScoreConfig.LoadOrDefault(null, Dir, out _));
        Assert.Matches("^[0-9a-f]{8}$", hash);
        Assert.All(rows, r => Assert.Equal(hash, r.ConfigHash));
        Assert.Equal(SectionSnapshotAnalyzer.ConfigHash(SurfaceScoreConfig.Default()), SectionSnapshotAnalyzer.ConfigHash(SurfaceScoreConfig.Default()));
        Assert.NotEqual(SectionSnapshotAnalyzer.ConfigHash(SurfaceScoreConfig.Default()), SectionSnapshotAnalyzer.ConfigHash(LowThresholds()));
    }

    [Fact]
    public async Task Tokens_SplitIntoProdAndContracts_EachTaggedWithItsSource()
    {
        var rows = await SectionSnapshotAnalyzer.AnalyzeAsync(_fixture.Solution, null, CancellationToken.None);
        var report = await new SurfaceScoreEngine(SurfaceScoreConfig.Default(), Dir).ScoreAsync(_fixture.Solution, CancellationToken.None);

        var camp = rows.Single(r => r.Section == "Camp");
        var tokens = report.Tokens!.BySection["Camp"];
        Assert.True(camp.TokensContracts > 0 && camp.TokensProd > 0);
        Assert.Equal(tokens.Total.Value, camp.TokensProd + camp.TokensContracts);
        Assert.Equal(tokens.Tests.Value, camp.TokensTests);
        // No counter and an empty cache path: every figure is an estimate.
        Assert.All(rows.Where(r => r.TokensProd > 0), r => Assert.Equal("estimate", r.TokensProdSource));
        Assert.Equal("", rows.Single(r => r.Section == "Web").TokensContractsSource);

        var solution = rows[^1];
        Assert.Equal(report.Tokens.Total.Value, solution.TokensProd + solution.TokensContracts);
        Assert.Equal(Math.Round((double)tokens.Tests.Value / tokens.Total.Value, 3), camp.TestTokenRatio);
    }

    [Fact]
    public void TokenFigure_SumOfExactAndEstimatedParts_IsMixed()
    {
        var exact = new TokenFigure(10, false);
        var estimate = new TokenFigure(20, true);

        Assert.Equal("exact", TokenFigure.Sum([exact, exact]).Provenance);
        Assert.Equal("estimate", TokenFigure.Sum([estimate, estimate]).Provenance);
        var mixed = TokenFigure.Sum([exact, estimate]);
        Assert.Equal(("mixed", "estimate", 30), (mixed.Provenance, mixed.Source, mixed.Value));
        Assert.Equal("mixed", TokenFigure.Sum([mixed, estimate]).Provenance);
    }

    [Fact]
    public async Task Score_BlankUnlessAsked_AndThenTheEngines()
    {
        var config = LowThresholds();
        var plain = await SectionSnapshotAnalyzer.AnalyzeAsync(_fixture.Solution, null, CancellationToken.None, config: config);
        var scored = await SectionSnapshotAnalyzer.AnalyzeAsync(_fixture.Solution, null, CancellationToken.None, score: true, config: config);
        var report = await new SurfaceScoreEngine(config, Dir).ScoreAsync(_fixture.Solution, CancellationToken.None);

        Assert.All(plain, r => Assert.Null(r.ScoreTotal));
        Assert.Contains(",,,", SectionSnapshotAnalyzer.ToCsv(plain, header: false));
        foreach (var row in scored.Take(scored.Count - 1))
        {
            var group = report.Groups.GetValueOrDefault(row.Section);
            Assert.Equal((group?.SurfaceTotal ?? 0, group?.ImplementationShapeTotal ?? 0, group?.Total ?? 0),
                (row.ScoreSurface!.Value, row.ScoreImplementation!.Value, row.ScoreTotal!.Value));
            Assert.Equal(group?.ByRule.GetValueOrDefault("sectionSize") ?? 0, row.SectionSizeCharge);
            Assert.Equal(group?.ByRule.GetValueOrDefault("classSize") ?? 0, row.ClassSizeCharge);
        }
        Assert.Equal(report.Total, scored[^1].ScoreTotal);
        Assert.Equal(report.ByRule.GetValueOrDefault("sectionSize"), scored[^1].SectionSizeCharge);
        Assert.True(scored[^1].SectionSizeCharge > 0 && scored[^1].ClassSizeCharge > 0);

        // Everything but the score columns is the same either way.
        Assert.Equal(plain.Select(r => r with { ScoreSurface = null, ScoreImplementation = null, ScoreTotal = null }),
            scored.Select(r => r with { ScoreSurface = null, ScoreImplementation = null, ScoreTotal = null }));
    }

    [Fact]
    public async Task TestScore_IsKeptOutOfTheMainScore_AndCoverageMovesNothing()
    {
        var config = LowThresholds();
        var report = await new SurfaceScoreEngine(config, Dir).ScoreAsync(_fixture.Solution, CancellationToken.None);

        Assert.True(report.TestTotal > 0);
        Assert.False(report.ByRule.ContainsKey("testSize"));
        Assert.Equal(report.SurfaceTotal + report.ImplementationShapeTotal, report.Total);
        Assert.Equal(report.TestScoreBySection.Values.Sum(), report.TestTotal);

        var coverage = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(coverage, """
                <?xml version="1.0" encoding="utf-8"?>
                <coverage line-rate="0.1" branch-rate="0" version="1.9" timestamp="0">
                  <packages><package name="SampleSolution.Camp" line-rate="0.1" branch-rate="0" complexity="1">
                    <classes><class name="C" filename="C.cs" line-rate="0.1" branch-rate="0" complexity="1">
                      <methods /><lines><line number="1" hits="1" /><line number="2" hits="0" /></lines>
                    </class></classes>
                  </package></packages>
                </coverage>
                """);
            var without = await SectionSnapshotAnalyzer.AnalyzeAsync(_fixture.Solution, null, CancellationToken.None, score: true, config: config);
            var with = await SectionSnapshotAnalyzer.AnalyzeAsync(_fixture.Solution, coverage, CancellationToken.None, score: true, config: config);

            Assert.NotNull(with.Single(r => r.Section == "Camp").CoveragePercent);
            Assert.Equal(without.Select(r => (r.ScoreTotal, r.TestScore)), with.Select(r => (r.ScoreTotal, r.TestScore)));
            Assert.Equal(report.TestScoreBySection["Camp"], with.Single(r => r.Section == "Camp").TestScore);
            Assert.Equal(report.TestTotal, with[^1].TestScore);
        }
        finally { File.Delete(coverage); }
    }

    [Fact]
    public async Task SurfaceScoreJson_CarriesTheTestBlock_ApartFromTheTotal()
    {
        var report = await new SurfaceScoreEngine(LowThresholds(), Dir).ScoreAsync(_fixture.Solution, CancellationToken.None);
        var original = Console.Out;
        var sw = new StringWriter();
        Console.SetOut(sw);
        try { Commands.SurfaceScoreCommand.WriteJson(report, null, 5, 5, null); }
        finally { Console.SetOut(original); }

        using var doc = System.Text.Json.JsonDocument.Parse(sw.ToString());
        Assert.Equal(report.TestTotal, doc.RootElement.GetProperty("testTotal").GetInt32());
        Assert.Equal(report.TestTotal, doc.RootElement.GetProperty("tests").GetProperty("score").GetInt32());
        var camp = doc.RootElement.GetProperty("groups").EnumerateArray().Single(g => g.GetProperty("name").GetString() == "Camp").GetProperty("tests");
        Assert.Equal(report.TestScoreBySection["Camp"], camp.GetProperty("score").GetInt32());
        Assert.Equal(2, camp.GetProperty("methods").GetInt32());
        Assert.True(camp.GetProperty("tokenRatio").GetDouble() > 0);
        Assert.False(camp.TryGetProperty("coveragePercent", out _));
    }

    [Fact]
    public async Task TestMethods_CountedPerSection_AndPerProdClass()
    {
        var rows = await SectionSnapshotAnalyzer.AnalyzeAsync(_fixture.Solution, null, CancellationToken.None);

        var camp = rows.Single(r => r.Section == "Camp");
        Assert.Equal(2, camp.TestMethods);
        Assert.Equal(Math.Round(2.0 / camp.Classes, 3), camp.TestMethodsPerClass);
        Assert.True(rows[^1].TestMethods >= camp.TestMethods);
    }

    [Theory]
    [InlineData("[Fact]", true)]
    [InlineData("[Xunit.Theory, InlineData(1)]", true)]
    [InlineData("[NUnit.Framework.TestCase(1)]", true)]
    [InlineData("[TestMethodAttribute]", true)]
    [InlineData("[Obsolete]", false)]
    [InlineData("", false)]
    public void IsTestMethod_ReadsTheAttributeName(string attributes, bool expected)
    {
        var method = CSharpSyntaxTree.ParseText($"class C {{ {attributes} void M(int x) {{ }} }}")
            .GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().Single();
        Assert.Equal(expected, TestMassAnalyzer.IsTestMethod(method));
    }
}
