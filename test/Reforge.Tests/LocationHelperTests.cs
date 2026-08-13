namespace Reforge.Tests;

/// <summary>
/// Path normalization feeds classification path globs, the canonical-read-DTO contracts-surface
/// check, and every file path reported to the caller — so "is this path under the solution" has to
/// be containment, not a string prefix.
/// </summary>
public class LocationHelperTests
{
    [Theory]
    // Genuinely under the solution root, with and without a trailing separator on the root.
    [InlineData("/work/App", "/work/App/Section/Foo.cs", "Section/Foo.cs")]
    [InlineData("/work/App/", "/work/App/Section/Foo.cs", "Section/Foo.cs")]
    [InlineData(@"C:\work\App", @"C:\work\App\Section\Foo.cs", "Section/Foo.cs")]
    // A SIBLING directory that merely shares the root's string prefix. A bare StartsWith would
    // hand back "Contracts/Foo.cs" — a path that never existed, and one that then reads as a
    // contracts surface.
    [InlineData("/work/App", "/work/AppContracts/Foo.cs", "/work/AppContracts/Foo.cs")]
    [InlineData(@"C:\work\App", @"C:\work\AppContracts\Foo.cs", "C:/work/AppContracts/Foo.cs")]
    // Unrelated path: returned as-is, forward-slashed.
    [InlineData("/work/App", "/elsewhere/Foo.cs", "/elsewhere/Foo.cs")]
    public void NormalizePath_RequiresADirectoryBoundary(string solutionDir, string path, string expected)
    {
        Assert.Equal(expected, LocationHelper.NormalizePath(path, solutionDir));
    }

    [Fact]
    public void NormalizePath_EmptyInput_IsReturnedUnchanged()
    {
        Assert.Equal("", LocationHelper.NormalizePath("", "/work/App"));
        // The path IS the solution directory — nothing left once the root is stripped.
        Assert.Equal("", LocationHelper.NormalizePath("/work/App", "/work/App"));
    }
}
