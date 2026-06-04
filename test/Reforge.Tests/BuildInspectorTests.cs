using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Reforge.Tests;

public class BuildInspectorTests
{
    // Compile as a library: the default (console app) output kind would emit CS5001
    // ("no Main method"), which is exactly the kind of error CountErrors counts.
    private static Compilation Compile(string source) =>
        CSharpCompilation.Create("t",
            new[] { CSharpSyntaxTree.ParseText(source) },
            new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

    [Fact]
    public void CountErrors_CleanSource_IsZero()
    {
        var comp = Compile("public sealed class Ok { public int X { get; set; } }");
        var (errors, unresolved) = BuildInspector.CountErrors(new[] { comp }, CancellationToken.None);
        Assert.Equal(0, errors);
        Assert.Equal(0, unresolved);
    }

    [Fact]
    public void CountErrors_UnresolvedBaseType_CountsErrorAndUnresolved()
    {
        // `Undefined` is not declared -> CS0246 (type or namespace not found).
        var comp = Compile("public sealed class Broken : Undefined { }");
        var (errors, unresolved) = BuildInspector.CountErrors(new[] { comp }, CancellationToken.None);
        Assert.True(errors >= 1, $"expected >=1 error, got {errors}");
        Assert.True(unresolved >= 1, $"expected >=1 unresolved, got {unresolved}");
    }

    [Fact]
    public void AppearsUnbuilt_ProjectWithObjCsArtifacts_IsFalse()
    {
        var dir = Path.Combine(Path.GetTempPath(), "reforge-bi-built-" + Guid.NewGuid().ToString("N"));
        var objDir = Path.Combine(dir, "obj", "Debug", "net10.0");
        Directory.CreateDirectory(objDir);
        File.WriteAllText(Path.Combine(objDir, "Proj.GlobalUsings.g.cs"), "// generated");
        var projPath = Path.Combine(dir, "Proj.csproj");
        File.WriteAllText(projPath, "<Project/>");
        try
        {
            Assert.False(BuildInspector.AppearsUnbuilt(new[] { (string?)projPath }));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void AppearsUnbuilt_ProjectWithoutObjArtifacts_IsTrue()
    {
        var dir = Path.Combine(Path.GetTempPath(), "reforge-bi-unbuilt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var projPath = Path.Combine(dir, "Proj.csproj");
        File.WriteAllText(projPath, "<Project/>");
        try
        {
            Assert.True(BuildInspector.AppearsUnbuilt(new[] { (string?)projPath }));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void AppearsUnbuilt_NoKnownPaths_IsFalse()
    {
        Assert.False(BuildInspector.AppearsUnbuilt(new string?[] { null, "" }));
    }

    [Fact]
    public void DescribeDegraded_UnbuiltWording_MentionsDotnetBuild()
    {
        var h = new BuildHealth(Degraded: true, CompilationErrorCount: 142, UnresolvedReferenceCount: 37, AppearsUnbuilt: true);
        var msg = BuildInspector.DescribeDegraded(h);
        Assert.Contains("unbuilt", msg, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("dotnet build", msg, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("142", msg);
    }

    [Fact]
    public void DescribeDegraded_ErrorsButBuilt_MentionsCompileErrors()
    {
        var h = new BuildHealth(Degraded: true, CompilationErrorCount: 3, UnresolvedReferenceCount: 0, AppearsUnbuilt: false);
        var msg = BuildInspector.DescribeDegraded(h);
        Assert.Contains("3", msg);
        Assert.Contains("error", msg, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------- Build diagnostics: dedup / cap / ordering ----------------

    private static BuildDiagnostic Diag(string id, string project, string file, int line, string message) =>
        new(id, "Error", project, file, line, 1, message);

    [Fact]
    public void DedupAndCap_IdenticalIdFileLineMessage_CollapsesToOne()
    {
        var input = new[]
        {
            Diag("CS0246", "ProjA", "src/Foo.cs", 12, "type 'Bar' not found"),
            Diag("CS0246", "ProjA", "src/Foo.cs", 12, "type 'Bar' not found"),
        };

        var (diags, truncated) = BuildInspector.DedupAndCap(input, max: 25);

        Assert.Single(diags);
        Assert.Equal(0, truncated);
    }

    [Fact]
    public void DedupAndCap_DifferingOnAnyKeyField_AreKept()
    {
        var input = new[]
        {
            Diag("CS0246", "ProjA", "src/Foo.cs", 12, "type 'Bar' not found"),
            Diag("CS0103", "ProjA", "src/Foo.cs", 12, "type 'Bar' not found"), // different id
            Diag("CS0246", "ProjA", "src/Foo.cs", 13, "type 'Bar' not found"), // different line
            Diag("CS0246", "ProjA", "src/Other.cs", 12, "type 'Bar' not found"), // different file
            Diag("CS0246", "ProjA", "src/Foo.cs", 12, "type 'Baz' not found"), // different message
        };

        var (diags, truncated) = BuildInspector.DedupAndCap(input, max: 25);

        Assert.Equal(5, diags.Count);
        Assert.Equal(0, truncated);
    }

    [Fact]
    public void DedupAndCap_OverCap_TruncatesAndReportsCount()
    {
        var input = Enumerable.Range(0, 5)
            .Select(i => Diag("CS0246", "ProjA", $"src/F{i}.cs", 1, "boom"))
            .ToArray();

        var (diags, truncated) = BuildInspector.DedupAndCap(input, max: 2);

        Assert.Equal(2, diags.Count);
        Assert.Equal(3, truncated);
    }

    [Fact]
    public void DedupAndCap_ZeroMax_IsUnlimited()
    {
        var input = Enumerable.Range(0, 40)
            .Select(i => Diag("CS0246", "ProjA", $"src/F{i}.cs", 1, "boom"))
            .ToArray();

        var (diags, truncated) = BuildInspector.DedupAndCap(input, max: 0);

        Assert.Equal(40, diags.Count);
        Assert.Equal(0, truncated);
    }

    [Fact]
    public void DedupAndCap_OrdersByProjectThenFileThenLine()
    {
        var input = new[]
        {
            Diag("CS1", "ProjB", "src/a.cs", 5, "m"),
            Diag("CS2", "ProjA", "src/b.cs", 1, "m"),
            Diag("CS3", "ProjA", "src/a.cs", 9, "m"),
            Diag("CS4", "ProjA", "src/a.cs", 2, "m"),
        };

        var (diags, _) = BuildInspector.DedupAndCap(input, max: 0);

        Assert.Collection(diags,
            d => Assert.Equal(("ProjA", "src/a.cs", 2), (d.Project, d.File, d.Line)),
            d => Assert.Equal(("ProjA", "src/a.cs", 9), (d.Project, d.File, d.Line)),
            d => Assert.Equal(("ProjA", "src/b.cs", 1), (d.Project, d.File, d.Line)),
            d => Assert.Equal(("ProjB", "src/a.cs", 5), (d.Project, d.File, d.Line)));
    }

    [Fact]
    public void CollectDiagnostics_UnresolvedType_CapturesCodeFileLineMessageProject()
    {
        var source = "public sealed class Broken : Undefined { }";
        var tree = CSharpSyntaxTree.ParseText(source, path: "/sln/src/Broken.cs");
        var comp = CSharpCompilation.Create("t",
            new[] { tree },
            new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var (diags, truncated) = BuildInspector.CollectDiagnostics(
            new (Compilation, string)[] { (comp, "MyProject") }, solutionDirectory: "/sln", max: 25, CancellationToken.None);

        Assert.Equal(0, truncated);
        var cs0246 = Assert.Single(diags, d => d.Id == "CS0246");
        Assert.Equal("MyProject", cs0246.Project);
        Assert.Equal("src/Broken.cs", cs0246.File); // solution-relative, forward slashes
        Assert.Equal(1, cs0246.Line);
        Assert.Contains("Undefined", cs0246.Message);
        Assert.Equal("Error", cs0246.Severity);
    }
}
