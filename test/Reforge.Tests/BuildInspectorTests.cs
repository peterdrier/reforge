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
}
