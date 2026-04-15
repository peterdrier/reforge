using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Reforge;

public sealed record SnapshotRecord(
    string CommitDate,
    string Commit,
    string Solution,
    int LocProd,
    int LocTest,
    int FilesProd,
    int FilesTest,
    int Classes,
    int Interfaces,
    double PropagationCost,
    double CoreSizePct,
    int CoreFileCount,
    int CycleCount,
    double AvgFanOut,
    int MaxFanOut,
    string MaxFanOutFile,
    double AvgCyclomatic,
    int P95Cyclomatic,
    int MaxCyclomatic,
    string MaxCyclomaticMethod,
    double AvgClassLoc,
    int P95ClassLoc,
    int MaxClassLoc,
    string MaxClassLocName
);

public static class SnapshotAnalyzer
{
    public static async Task<(SnapshotRecord Record, FileDependencyGraph Graph, List<int[]> Sccs)> AnalyzeAsync(
        Solution solution, CancellationToken ct)
    {
        var graph = await FileDependencyGraph.BuildAsync(solution, ct);

        // SCCs & propagation cost.
        var sccs = StructuralAnalysis.FindStronglyConnectedComponents(graph.Adj);
        var (propagationCost, _) = StructuralAnalysis.ComputePropagationCost(graph.Adj, sccs);

        // Core = largest non-trivial SCC.
        var core = StructuralAnalysis.FindCoreScc(sccs);
        int n = graph.Files.Count;
        double coreSizePct = n > 0 ? (double)core.Length / n : 0;
        int cycleCount = sccs.Count(s => s.Length >= 2);

        // Fan-out stats from the adjacency list.
        double avgFanOut = 0;
        int maxFanOut = 0;
        string maxFanOutFile = "";
        if (n > 0)
        {
            long total = 0;
            for (int i = 0; i < n; i++)
            {
                int f = graph.Adj[i].Count;
                total += f;
                if (f > maxFanOut)
                {
                    maxFanOut = f;
                    maxFanOutFile = graph.Files[i];
                }
            }
            avgFanOut = (double)total / n;
        }

        // Cyclomatic complexity and per-class LOC: walk every prod method/type once.
        var ccValues = new List<int>();
        int ccMax = 0;
        string ccMaxMethod = "";
        var classLocs = new List<int>();
        int classLocMax = 0;
        string classLocMaxName = "";

        foreach (var project in solution.Projects)
        {
            if (ct.IsCancellationRequested) break;
            if (IsTestProject(project)) continue;

            var compilation = await project.GetCompilationAsync(ct);
            if (compilation is null) continue;

            foreach (var tree in compilation.SyntaxTrees)
            {
                var path = tree.FilePath ?? "";
                if (IsExcludedPath(path)) continue;

                var root = await tree.GetRootAsync(ct);

                foreach (var typeDecl in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
                {
                    var span = typeDecl.GetLocation().GetLineSpan();
                    int lines = span.EndLinePosition.Line - span.StartLinePosition.Line + 1;
                    classLocs.Add(lines);
                    if (lines > classLocMax)
                    {
                        classLocMax = lines;
                        classLocMaxName = typeDecl.Identifier.Text;
                    }
                }

                foreach (var method in root.DescendantNodes().OfType<BaseMethodDeclarationSyntax>())
                {
                    SyntaxNode? body = method switch
                    {
                        MethodDeclarationSyntax m => (SyntaxNode?)m.Body ?? m.ExpressionBody,
                        ConstructorDeclarationSyntax c => (SyntaxNode?)c.Body ?? c.ExpressionBody,
                        _ => null
                    };
                    if (body is null) continue;

                    int cc = ComputeCyclomaticComplexity(body);
                    ccValues.Add(cc);
                    if (cc > ccMax)
                    {
                        ccMax = cc;
                        ccMaxMethod = method switch
                        {
                            MethodDeclarationSyntax m =>
                                $"{FindContainingType(m)}.{m.Identifier.Text}",
                            ConstructorDeclarationSyntax c =>
                                $"{FindContainingType(c)}.ctor",
                            _ => "?"
                        };
                    }
                }
            }
        }

        ccValues.Sort();
        classLocs.Sort();

        double avgCc = ccValues.Count > 0 ? ccValues.Average() : 0;
        int p95Cc = StructuralAnalysis.Percentile(ccValues, 0.95);
        double avgClassLoc = classLocs.Count > 0 ? classLocs.Average() : 0;
        int p95ClassLoc = StructuralAnalysis.Percentile(classLocs, 0.95);

        var record = new SnapshotRecord(
            CommitDate: TryGetGitCommitDate(solution),
            Commit: TryGetGitCommit(solution),
            Solution: Path.GetFileName(solution.FilePath ?? ""),
            LocProd: graph.TotalProdLoc,
            LocTest: graph.TotalTestLoc,
            FilesProd: graph.Files.Count,
            FilesTest: graph.TestFileCount,
            Classes: graph.ClassCount,
            Interfaces: graph.InterfaceCount,
            PropagationCost: propagationCost,
            CoreSizePct: coreSizePct,
            CoreFileCount: core.Length,
            CycleCount: cycleCount,
            AvgFanOut: avgFanOut,
            MaxFanOut: maxFanOut,
            MaxFanOutFile: maxFanOutFile,
            AvgCyclomatic: avgCc,
            P95Cyclomatic: p95Cc,
            MaxCyclomatic: ccMax,
            MaxCyclomaticMethod: ccMaxMethod,
            AvgClassLoc: avgClassLoc,
            P95ClassLoc: p95ClassLoc,
            MaxClassLoc: classLocMax,
            MaxClassLocName: classLocMaxName
        );

        return (record, graph, sccs);
    }

    private static int ComputeCyclomaticComplexity(SyntaxNode methodBody)
    {
        int complexity = 1;
        foreach (var node in methodBody.DescendantNodes())
        {
            complexity += node switch
            {
                IfStatementSyntax => 1,
                ElseClauseSyntax { Statement: IfStatementSyntax } => 0,
                CaseSwitchLabelSyntax => 1,
                CasePatternSwitchLabelSyntax => 1,
                SwitchExpressionArmSyntax => 1,
                ConditionalExpressionSyntax => 1,
                ForStatementSyntax => 1,
                ForEachStatementSyntax => 1,
                WhileStatementSyntax => 1,
                DoStatementSyntax => 1,
                CatchClauseSyntax => 1,
                BinaryExpressionSyntax b when b.IsKind(SyntaxKind.LogicalAndExpression) => 1,
                BinaryExpressionSyntax b when b.IsKind(SyntaxKind.LogicalOrExpression) => 1,
                BinaryExpressionSyntax b when b.IsKind(SyntaxKind.CoalesceExpression) => 1,
                ConditionalAccessExpressionSyntax => 1,
                _ => 0
            };
        }
        return complexity;
    }

    private static string FindContainingType(SyntaxNode node)
    {
        var current = node.Parent;
        while (current is not null)
        {
            if (current is TypeDeclarationSyntax t) return t.Identifier.Text;
            current = current.Parent;
        }
        return "?";
    }

    private static bool IsTestProject(Project project) =>
        project.Name.Contains("Test", StringComparison.OrdinalIgnoreCase) ||
        project.Name.Contains("Spec", StringComparison.OrdinalIgnoreCase);

    private static bool IsExcludedPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return true;
        if (path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
            path.Contains("/obj/")) return true;
        if (path.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}") ||
            path.Contains("/Migrations/")) return true;
        return false;
    }

    private static string TryGetGitCommit(Solution solution) =>
        RunGit(solution, "rev-parse --short HEAD");

    private static string TryGetGitCommitDate(Solution solution) =>
        RunGit(solution, "show -s --format=%cI HEAD");

    private static string RunGit(Solution solution, string arguments)
    {
        try
        {
            var dir = LocationHelper.GetSolutionDirectory(solution);
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "git",
                Arguments = arguments,
                WorkingDirectory = dir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc is null) return "";
            if (!proc.WaitForExit(2000))
            {
                try { proc.Kill(); } catch { }
                return "";
            }
            return proc.ExitCode == 0 ? proc.StandardOutput.ReadToEnd().Trim() : "";
        }
        catch
        {
            return "";
        }
    }
}
