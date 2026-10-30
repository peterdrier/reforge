using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Reforge.Tests;

/// <summary>
/// What the contents census counts as a declaration, and the curve it prices complexity with.
/// </summary>
public class ContentsPricingTests
{
    private static readonly SurfaceScoreConfig Defaults = SurfaceScoreConfig.Default();

    private static double F(int n) => ContentsPricing.Curve(n,
        Defaults.ComplexityThreshold, Defaults.ComplexityTaxPercent, Defaults.ComplexityTaxExponent);

    [Fact]
    public void Curve_IsLinearUpToTheThreshold_AndNeverBelowItsInput()
    {
        for (int n = 0; n <= Defaults.ComplexityThreshold; n++)
            Assert.Equal(n, F(n));
        for (int n = 0; n < 200; n++)
            Assert.True(F(n) >= n, $"f({n}) below {n}");
    }

    [Fact]
    public void Curve_NeverChargesTheSameLogicLessInOnePiece()
    {
        for (int a = 0; a < 60; a++)
            for (int b = 0; b < 60; b++)
                Assert.True(F(a + b) >= F(a) + F(b), $"f({a + b}) below f({a}) + f({b})");
    }

    /// <summary>
    /// The arithmetic behind the methodSize Gate 1 trio: 50 points of complexity cost least as five
    /// pieces at the threshold, more as one monolith, and most as 25 slivers.
    /// </summary>
    [Fact]
    public void FiftyPoints_CostLeastNearTheThreshold()
    {
        int m = Defaults.Weight("methodCount");
        double Arrangement(int pieces, int each) => pieces * (F(each) + m);

        double monolith = Arrangement(1, 50), middle = Arrangement(5, 10), slivers = Arrangement(25, 2);
        Assert.True(middle < monolith, $"5x10 {middle} not below 1x50 {monolith}");
        Assert.True(monolith <= slivers, $"25x2 {slivers} below 1x50 {monolith}");
    }

    [Fact]
    public void Units_CountEveryDeclarationThatCarriesCode()
    {
        var type = Compile("""
            using System;
            using System.Linq;
            public class C
            {
                private int _x;
                public int Auto { get; set; }
                public int Computed => _x > 0 ? _x : 0;
                public int Backed { get => _x; set => _x = value; }
                private static readonly Func<int, int> Twice = v => v * 2;
                private static readonly int Plain = 3;
                public C() { _x = 1; }
                public int Run(int[] xs)
                {
                    Func<int, bool> keep = v => v > 0;
                    int Local(int v) => v + 1;
                    return xs.Where(v => v > 1).Select(Local).Count(keep);
                }
                public abstract class Nested { public abstract void Abstract(); }
            }
            """);

        var units = ContentsPricing.Units(type, CancellationToken.None);
        var names = units.Select(u => u.Symbol.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();

        // Auto-properties, plain fields and the nested type's abstract member carry no code.
        Assert.Equal(new[] { ".ctor", "Backed", "Computed", "Run", "Twice" }, names);
        // The local function and the lambda held in a local are declarations; the LINQ argument is not.
        Assert.Equal(2, units.Single(u => u.Symbol.Name == "Run").LocalFunctions);
        Assert.Equal(1, units.Single(u => u.Symbol.Name == "Computed").Cognitive.Score);
    }

    /// <summary>
    /// Every declaration lands in exactly one complexity band, local functions and locally held
    /// lambdas included, so the bands sum to the declaration count.
    /// </summary>
    [Fact]
    public void Census_BandsSumToDeclarations()
    {
        var type = Compile("""
            using System;
            public class C
            {
                public event Action<int> Changed = v => { if (v > 0) Console.WriteLine(v); };
                public int Run(int[] xs)
                {
                    Func<int, bool> keep = v => v > 0 && v < 10;
                    int Local(int v)
                    {
                        if (v > 1) { for (int i = 0; i < v; i++) { if (i % 2 == 0) v++; } }
                        return v;
                    }
                    return keep(xs[0]) ? Local(xs[0]) : 0;
                }
            }
            """);

        var units = ContentsPricing.Units(type, CancellationToken.None);
        var census = ContentsCensus.Empty;
        foreach (var u in units) census.Add(u);

        Assert.Contains(units, u => u.Symbol.Name == "Changed" && u.Cognitive.Score > 0);
        Assert.Equal(4, census.Declarations);
        Assert.Equal(census.Declarations, census.Bands.Sum());
    }

    private static INamedTypeSymbol Compile(string source)
    {
        var refs = (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Where(p => p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .Select(p => MetadataReference.CreateFromFile(p));
        var compilation = CSharpCompilation.Create("Probe",
            new[] { CSharpSyntaxTree.ParseText(source, path: "C.cs") }, refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        Assert.Empty(compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
        return compilation.GetTypeByMetadataName("C")!;
    }
}
