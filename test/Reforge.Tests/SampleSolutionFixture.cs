using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;

namespace Reforge.Tests;

public class SampleSolutionFixture : IAsyncLifetime
{
    public Solution Solution { get; private set; } = null!;
    public MSBuildWorkspace Workspace { get; private set; } = null!;

    static SampleSolutionFixture()
    {
        // Must register before any Roslyn types are used
        if (!MSBuildLocator.IsRegistered)
            MSBuildLocator.RegisterDefaults();
    }

    public async Task InitializeAsync()
    {
        var testDir = Path.GetDirectoryName(typeof(SampleSolutionFixture).Assembly.Location)!;
        var repoRoot = FindRepoRoot(testDir);
        var solutionPath = Path.Combine(repoRoot, "test", "SampleSolution", "SampleSolution.slnx");

        Workspace = MSBuildWorkspace.Create();
        Workspace.RegisterWorkspaceFailedHandler(e =>
        {
            // Log to stderr so it doesn't interfere with test output
            Console.Error.WriteLine($"workspace: {e.Diagnostic.Message}");
        });

        Solution = await Workspace.OpenSolutionAsync(solutionPath);
    }

    public Task DisposeAsync()
    {
        Workspace?.Dispose();
        return Task.CompletedTask;
    }

    private static string FindRepoRoot(string start)
    {
        var dir = new DirectoryInfo(start);
        while (dir != null)
        {
            if (dir.GetDirectories(".git").Length > 0)
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Could not find repo root");
    }
}

[CollectionDefinition("SampleSolution")]
public class SampleSolutionCollection : ICollectionFixture<SampleSolutionFixture> { }
