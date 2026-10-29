using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Reforge.Tests;

/// <summary>
/// What the contents census counts as a declaration, and the curve it prices complexity with.
/// </summary>
public class ContentsPricingTests
{
    [Fact]
    public void Curve_IsZeroForStraightLineCode_AndNeverBelowItsInput()
    {
        Assert.Equal(0, ContentsPricing.Curve(0));
        Assert.Equal(1, ContentsPricing.Curve(1));
        for (int n = 1; n < 200; n++)
            Assert.True(ContentsPricing.Curve(n) >= n, $"f({n}) below {n}");
    }

    [Fact]
    public void Curve_IsConvex_SoOneLargeMethodCostsMoreThanItsParts()
    {
        for (int a = 1; a < 50; a++)
            for (int b = 1; b < 50; b++)
                Assert.True(ContentsPricing.Curve(a + b) > ContentsPricing.Curve(a) + ContentsPricing.Curve(b));
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
