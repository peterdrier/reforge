using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;

namespace Reforge;

public static class WorkspaceHelper
{
    /// <summary>
    /// Opens a solution and returns the Solution + MSBuildWorkspace.
    /// The caller is responsible for disposing the workspace.
    /// </summary>
    /// <param name="solutionPath">
    /// Explicit path to a .slnx or .sln file. If null, searches upward from CWD.
    /// </param>
    public static async Task<(Solution solution, MSBuildWorkspace workspace)> OpenSolutionAsync(string? solutionPath)
    {
        var resolved = solutionPath ?? FindSolutionFile();

        var workspace = MSBuildWorkspace.Create();
        workspace.RegisterWorkspaceFailedHandler(e =>
        {
            Console.Error.WriteLine($"workspace: {e.Diagnostic.Message}");
        });

        try
        {
            var solution = await workspace.OpenSolutionAsync(resolved);
            return (solution, workspace);
        }
        catch
        {
            workspace.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Searches upward from CWD for a solution file.
    /// Prefers .slnx over .sln. Errors if multiple candidates exist in the same directory.
    /// </summary>
    private static string FindSolutionFile()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());

        while (dir is not null)
        {
            // Prefer .slnx
            var slnxFiles = dir.GetFiles("*.slnx");
            if (slnxFiles.Length == 1)
                return slnxFiles[0].FullName;
            if (slnxFiles.Length > 1)
            {
                var candidates = string.Join(Environment.NewLine, slnxFiles.Select(f => f.FullName));
                throw new InvalidOperationException(
                    $"Multiple .slnx files found in {dir.FullName}. Specify one with --solution:{Environment.NewLine}{candidates}");
            }

            // Fall back to .sln
            var slnFiles = dir.GetFiles("*.sln");
            if (slnFiles.Length == 1)
                return slnFiles[0].FullName;
            if (slnFiles.Length > 1)
            {
                var candidates = string.Join(Environment.NewLine, slnFiles.Select(f => f.FullName));
                throw new InvalidOperationException(
                    $"Multiple .sln files found in {dir.FullName}. Specify one with --solution:{Environment.NewLine}{candidates}");
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            "No .slnx or .sln file found in the current directory or any parent directory. Specify one with --solution.");
    }
}
