using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Reforge.Tests;

/// <summary>Who the contents graph says reaches a declaration.</summary>
public class ContentsGraphTests
{
    [Fact]
    public async Task OperatorsConversionsAndIndexers_HaveTheirUsersAsCallers()
    {
        var (graph, members) = await BuildAsync("""
            public readonly struct Money
            {
                private readonly int _cents;
                public Money(int cents) { _cents = cents; }
                public static Money operator +(Money a, Money b) => new Money(a._cents + b._cents);
                public static Money operator -(Money a) => new Money(-a._cents);
                public static implicit operator Money(int cents) => new Money(cents);
                public static explicit operator int(Money m) => m._cents;
                public int this[int i] => i == 0 ? _cents : 0;
            }

            public static class Ledger
            {
                public static Money Sum(Money a, Money b) => a + b;
                public static Money Negate(Money a) => -a;
                public static Money Raise(Money a) { a += a; return a; }
                public static Money FromInt(int cents) { Money m = cents; return m; }
                public static int ToInt(Money m) => (int)m;
                public static int Cents(Money m) => m[0];
                public static int MaybeCents(Money? m) => m?[0] ?? 0;
            }
            """);

        Assert.Equal(2, graph.Callers(members["op_Addition"]));
        Assert.Equal(1, graph.Callers(members["op_UnaryNegation"]));
        Assert.Equal(1, graph.Callers(members["op_Implicit"]));
        Assert.Equal(1, graph.Callers(members["op_Explicit"]));
        Assert.Equal(2, graph.Callers(members["this[]"]));
        Assert.Equal(1, graph.Depth(members["op_Addition"]));
    }

    [Fact]
    public async Task ABaseCall_DoesNotReachSiblingOverrides()
    {
        var (graph, members) = await BuildAsync("""
            public abstract class Shape
            {
                public virtual int Area() => 0;
            }

            public class Square : Shape
            {
                public override int Area() => base.Area() + 1;
            }

            public class Circle : Shape
            {
                public override int Area() => base.Area() + 2;
            }

            public static class Geometry
            {
                public static int Measure(Shape s) => s.Area();
            }
            """, typed: true);

        Assert.Equal(3, graph.Callers(members["Shape.Area"]));
        Assert.Equal(1, graph.Callers(members["Square.Area"]));
        Assert.Equal(1, graph.Callers(members["Circle.Area"]));
    }

    [Fact]
    public async Task OnACycle_ADeclarationDoesNotReachItself()
    {
        var (graph, members) = await BuildAsync("""
            public static class Cycle
            {
                public static int Ping(int n) => n <= 0 ? 0 : Pong(n - 1);
                public static int Pong(int n) => n <= 0 ? 0 : Ping(n - 1);
            }
            """);

        Assert.Equal(1, graph.Reach(members["Ping"]));
        Assert.Equal(1, graph.Reach(members["Pong"]));
    }

    /// <summary>
    /// Builds the graph over every census declaration in <paramref name="source"/>, keyed by member
    /// name, or by <c>Type.Member</c> when <paramref name="typed"/>.
    /// </summary>
    private static async Task<(ContentsGraph Graph, Dictionary<string, ISymbol> Members)> BuildAsync(string source, bool typed = false)
    {
        var refs = (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Where(p => p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .Select(p => MetadataReference.CreateFromFile(p));
        using var workspace = new AdhocWorkspace();
        var project = workspace.AddProject(ProjectInfo.Create(ProjectId.CreateNewId(), VersionStamp.Default,
            "Probe", "Probe", LanguageNames.CSharp,
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            metadataReferences: refs));
        project = project.AddDocument("Probe.cs", source, filePath: "Probe.cs").Project;

        var compilation = (await project.GetCompilationAsync())!;
        Assert.Empty(compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
        var units = compilation.GlobalNamespace.GetTypeMembers()
            .SelectMany(t => ContentsPricing.Units(t, CancellationToken.None))
            .ToList();
        var graph = await ContentsGraph.BuildAsync(project.Solution, units,
            new HashSet<string>(StringComparer.Ordinal) { "Probe" }, CancellationToken.None);
        var members = units.ToDictionary(u => typed ? $"{u.Symbol.ContainingType.Name}.{u.Symbol.Name}" : u.Symbol.Name,
            u => u.Symbol);
        return (graph, members);
    }
}
