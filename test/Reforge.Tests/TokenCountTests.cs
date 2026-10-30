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
        Assert.Equal(99 + 49 + 29, core.Total);
        Assert.Equal(3, core.Files);
        Assert.Equal(core.Total, census.Total);
        Assert.Equal(["Core.A", "Core.Nested"], core.Classes.Select(x => x.Name));
        Assert.Equal(79 + 39, core.Classes[0].Tokens);
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

        Assert.True(after.BySection["S"].Total >= before.BySection["S"].Total);
    }

    [Fact]
    public async Task SecondRun_IsServedFromCache()
    {
        var path = Path.Combine(Path.GetTempPath(), $"reforge-tokens-{Guid.NewGuid():N}.json");
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
            Assert.Equal("class A { int x; }".Length - 1, census.Total);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task FailedCount_LeavesTotalsNull_AndIsNotCached()
    {
        var types = new List<TypeText>
        {
            Type("S", "S.Ok", true, [("Ok.cs", "class Ok { }")], "class Ok { }"),
            Type("S", "S.Bad", true, [("Bad.cs", "class Bad { }")], "class Bad { }"),
        };
        var cache = TokenCache.InMemory();
        var measurer = new TokenMeasurer(new FakeCounter { Fails = t => t.Contains("Bad") }, cache);

        var census = await TokenCensus.MeasureAsync(types, measurer, CancellationToken.None);

        Assert.Null(census.Total);
        Assert.Null(census.BySection["S"].Total);
        Assert.Null(census.BySection["S"].Classes.Single(c => c.Name == "S.Bad").Tokens);
        Assert.Equal(11, census.BySection["S"].Classes.Single(c => c.Name == "S.Ok").Tokens);
        Assert.Equal(1, measurer.Failures);
        Assert.Equal(2, cache.Count);
    }

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
