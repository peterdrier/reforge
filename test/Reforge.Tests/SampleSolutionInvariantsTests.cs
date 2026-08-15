namespace Reforge.Tests;

/// <summary>
/// Pins the sample solution's load-bearing property: it declares no NuGet package references.
///
/// <para><c>docs/simplify/as-is.md</c> records this as deliberate — "adding a package reference to
/// the fixture will break build-health tests in ways that look unrelated" — but nothing enforced
/// it, so the invariant lived only in a document. It is what lets <c>MSBuildWorkspace</c> open the
/// sample with zero compilation errors on a machine that never restored or built it, which in turn
/// is why <c>ScoreAsync_BuiltSampleSolution_IsNotDegraded</c> passes on a pristine CI checkout.</para>
///
/// <para>The tempting alternative — having CI restore and build the sample before running tests —
/// would make this test's failure mode invisible: a package reference added later would be
/// restored by that step, CI would stay green, and the first symptom would be an unrelated-looking
/// build-health failure on someone's machine. Fail here instead, naming the file.</para>
/// </summary>
public class SampleSolutionInvariantsTests
{
    [Fact]
    public void SampleSolutionProjects_DeclareNoPackageReferences()
    {
        var testDir = Path.GetDirectoryName(typeof(SampleSolutionInvariantsTests).Assembly.Location)!;
        var sampleDir = Path.Combine(SampleSolutionFixture.FindRepoRoot(testDir), "test", "SampleSolution");

        Assert.True(Directory.Exists(sampleDir), $"Sample solution directory not found at {sampleDir}");

        var offenders = Directory
            .EnumerateFiles(sampleDir, "*.csproj", SearchOption.AllDirectories)
            .Where(p => File.ReadAllText(p).Contains("<PackageReference", StringComparison.OrdinalIgnoreCase))
            .Select(p => Path.GetRelativePath(sampleDir, p))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        Assert.True(offenders.Count == 0,
            "The sample solution must declare no PackageReference — that is what keeps MSBuildWorkspace "
            + "loads clean without a restore, and several build-health tests depend on it. Found one in: "
            + string.Join(", ", offenders));
    }
}
