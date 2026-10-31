using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Reforge.Tests;

/// <summary>
/// Token aggregation with a fake counter: one token per character plus a fixed request overhead,
/// so expected values are exact and no API key is needed.
/// </summary>
public class TokenCountTests
{
    private const int Overhead = 7;

    private sealed class FakeCounter : ITokenCounter
    {
        public int Calls;
        public string Model => "fake";
        public Func<string, bool> Fails { get; init; } = _ => false;

        public Task<int?> CountAsync(string text, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(Fails(text) ? null : (int?)(text.Length + Overhead));
        }
    }

    private static TypeText Type(string section, string name, bool isClass, (string, string)[] files, params string[] spans) =>
        new(section, section, name, files[0].Item1, 1, isClass, files, spans);

    [Fact]
    public async Task BaselineIsSubtracted_SoFileCountsAddUp()
    {
        var measurer = new TokenMeasurer(new FakeCounter(), TokenCache.InMemory());

        Assert.Equal(9, await measurer.MeasureAsync("0123456789", CancellationToken.None));
    }

    [Fact]
    public async Task ClassSumsPartialSpans_SectionSumsWholeFilesOnce()
    {
        var a = ("A.cs", new string('a', 100));
        var b = ("A.Part.cs", new string('b', 50));
        var c = ("C.cs", new string('c', 30));
        var types = new List<TypeText>
        {
            Type("Core", "Core.A", true, [a, b], new string('x', 80), new string('y', 40)),
            Type("Core", "Core.E", false, [c]),
            Type("Core", "Core.Nested", true, [a], new string('z', 10)),
        };

        var census = await TokenCensus.MeasureAsync(types, new TokenMeasurer(new FakeCounter(), TokenCache.InMemory()), CancellationToken.None);

        var core = census.BySection["Core"];
        // Baseline "." is 1 char: every measurement is length - 1.
        Assert.Equal(new TokenFigure(99 + 49 + 29, false), core.Total);
        Assert.Equal(3, core.Files);
        Assert.Equal(core.Total, census.Total);
        Assert.Equal(["Core.A", "Core.Nested"], core.Classes.Select(x => x.Name));
        Assert.Equal(79 + 39, core.Classes[0].Tokens.Value);
    }

    [Fact]
    public async Task SplittingIntoPartialsOrSiblings_DoesNotLowerSectionTotal()
    {
        // Every file repeats its namespace line, as a real split does.
        const string header = "namespace S;\n";
        var body = new string('w', 200);
        var one = new List<TypeText> { Type("S", "S.Big", true, [("Big.cs", header + body)], body) };
        var split = new List<TypeText>
        {
            Type("S", "S.Big", true, [("Big.cs", header + body[..100]), ("Big.2.cs", header + body[100..])], body[..50], body[100..]),
            Type("S", "S.Sibling", true, [("Big.cs", header + body[..100])], body[50..100]),
        };

        var before = await TokenCensus.MeasureAsync(one, new TokenMeasurer(new FakeCounter(), TokenCache.InMemory()), CancellationToken.None);
        var after = await TokenCensus.MeasureAsync(split, new TokenMeasurer(new FakeCounter(), TokenCache.InMemory()), CancellationToken.None);

        Assert.True(after.BySection["S"].Total.Value >= before.BySection["S"].Total.Value);
    }

    [Fact]
    public async Task SecondRun_IsServedFromCache()
    {
        var path = Path.Combine(Path.GetTempPath(), $"reforge-tokens-{Guid.NewGuid():N}.txt");
        try
        {
            var types = new List<TypeText> { Type("S", "S.A", true, [("A.cs", "class A { int x; }")], "class A { int x; }") };

            var first = new FakeCounter();
            var cache = TokenCache.Load(path);
            await TokenCensus.MeasureAsync(types, new TokenMeasurer(first, cache), CancellationToken.None);
            cache.Save();
            // Baseline and one text: the class span equals the file, so it is asked for once.
            Assert.Equal(2, first.Calls);

            var second = new FakeCounter();
            var measurer = new TokenMeasurer(second, TokenCache.Load(path));
            var census = await TokenCensus.MeasureAsync(types, measurer, CancellationToken.None);
            Assert.Equal(0, second.Calls);
            Assert.Equal(0, measurer.Requests);
            Assert.Equal(new TokenFigure("class A { int x; }".Length - 1, false), census.Total);
        }
        finally { File.Delete(path); File.Delete(path + ".lock"); }
    }

    [Fact]
    public async Task FailedCount_IsEstimated_AndIsNotCached()
    {
        var types = new List<TypeText>
        {
            Type("S", "S.Ok", true, [("Ok.cs", "class Ok { }")], "class Ok { }"),
            Type("S", "S.Bad", true, [("Bad.cs", "class Bad { }")], "class Bad { }"),
        };
        var cache = TokenCache.InMemory();
        var measurer = new TokenMeasurer(new FakeCounter { Fails = t => t.Contains("Bad") }, cache);

        var census = await TokenCensus.MeasureAsync(types, measurer, CancellationToken.None);

        var bad = census.BySection["S"].Classes.Single(c => c.Name == "S.Bad").Tokens;
        Assert.Equal(new TokenFigure(TokenEstimator.Estimate("class Bad { }"), true), bad);
        Assert.Equal(new TokenFigure(11, false), census.BySection["S"].Classes.Single(c => c.Name == "S.Ok").Tokens);
        Assert.Equal(new TokenFigure(11 + bad.Value, true) { Mixed = true }, census.Total);
        Assert.Equal(1, measurer.Failures);
        Assert.Equal(2, cache.Count);
    }

    [Fact]
    public async Task WithoutAKey_CachedTextIsExact_PerFile_AndTheRestIsEstimated()
    {
        const string cached = "class Cached { }";
        const string fresh = "class Fresh { int x; }";
        var cache = TokenCache.InMemory();
        var model = AnthropicTokenCounter.DefaultModel;
        cache.Set(TokenCache.Key(model, TokenMeasurer.BaselineText), 8);
        cache.Set(TokenCache.Key(model, cached), 1008); // far from any estimate, so a mix-up shows
        var types = new List<TypeText>
        {
            Type("S", "S.Cached", true, [("Cached.cs", cached)], cached),
            Type("S", "S.Fresh", true, [("Fresh.cs", fresh)], fresh),
        };

        var census = await TokenCensus.MeasureAsync(types, new TokenMeasurer(null, cache), CancellationToken.None);

        var classes = census.BySection["S"].Classes.ToDictionary(c => c.Name, c => c.Tokens);
        Assert.Equal(new TokenFigure(1000, false), classes["S.Cached"]);
        Assert.Equal(new TokenFigure(TokenEstimator.Estimate(fresh), true), classes["S.Fresh"]);
        Assert.Equal(new TokenFigure(1000 + TokenEstimator.Estimate(fresh), true) { Mixed = true }, census.BySection["S"].Total);
        Assert.Equal(("estimate", "mixed"), (census.BySection["S"].Total.Source, census.BySection["S"].Total.Provenance));
    }

    [Fact]
    public async Task NoMeasurer_EstimatesEverything()
    {
        var types = new List<TypeText> { Type("S", "S.A", true, [("A.cs", "class A { }")], "class A { }") };

        var census = await TokenCensus.MeasureAsync(types, null, CancellationToken.None);

        Assert.Equal(new TokenFigure(TokenEstimator.Estimate("class A { }"), true), census.Total);
    }

    [Theory]
    [InlineData(7_600, true, true)]    // 5% under 8k
    [InlineData(8_400, true, true)]    // 5% over
    [InlineData(8_401, true, false)]
    [InlineData(23_800, true, true)]   // near the second threshold
    [InlineData(8_000, false, false)]  // exact figures are trusted on either side
    public void NearThreshold_FlagsOnlyEstimatesWithinFivePercent(int value, bool estimated, bool near) =>
        Assert.Equal(near, new TokenFigure(value, estimated).Flag(8_000, 25_000).NearThreshold);

    [Fact]
    public void Collect_TakesEveryPartialSpan_AndSkipsGeneratedFiles()
    {
        const string first = "namespace N;\n/// <summary>Doc.</summary>\npublic partial class P { void A() { } }\n";
        const string second = "namespace N;\npublic partial class P { void B() { } }\n";
        const string generated = "namespace N;\npublic partial class P { void G() { } }\n";
        var compilation = CSharpCompilation.Create("Asm",
            [
                CSharpSyntaxTree.ParseText(first, path: "/src/P.cs"),
                CSharpSyntaxTree.ParseText(second, path: "/src/P.More.cs"),
                CSharpSyntaxTree.ParseText(generated, path: "/src/P.g.cs"),
            ],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]);
        var type = compilation.GetTypeByMetadataName("N.P")!;
        var classified = new ClassifiedType(type, "Sec", [], "/src/P.cs", type.Locations[0]);

        var text = Assert.Single(TokenCensus.Collect([classified], CancellationToken.None));

        Assert.Equal(["/src/P.cs", "/src/P.More.cs"], text.Files.Select(f => f.Path).Order());
        Assert.Contains(text.Spans, s => s.StartsWith("/// <summary>Doc.", StringComparison.Ordinal) && s.EndsWith("void A() { } }", StringComparison.Ordinal));
        Assert.Contains("public partial class P { void B() { } }", text.Spans);
        Assert.Equal(2, text.Spans.Count);
        Assert.True(text.IsClass);
        Assert.Equal("P", text.Name);
    }
}
