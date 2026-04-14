using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Reforge;

/// <summary>
/// File-level dependency graph of a solution. A node is a source file (prod only —
/// tests, generated, and migrations excluded). An edge A→B exists if any symbol
/// referenced in A is declared in B. This is the unit MacCormack-style structural
/// metrics operate on.
/// </summary>
public sealed class FileDependencyGraph
{
    /// <summary>Ordered file list. Indices are stable and used by all downstream algorithms.</summary>
    public IReadOnlyList<string> Files { get; }

    /// <summary>Adjacency: Adj[i] = set of file indices that file i depends on.</summary>
    public IReadOnlyList<HashSet<int>> Adj { get; }

    /// <summary>Reverse adjacency: RevAdj[i] = set of file indices that depend on file i.</summary>
    public IReadOnlyList<HashSet<int>> RevAdj { get; }

    /// <summary>Lines of code per file (non-blank).</summary>
    public IReadOnlyList<int> Loc { get; }

    /// <summary>Prod LOC totals (sum of Loc).</summary>
    public int TotalProdLoc { get; }

    /// <summary>Test project LOC (excluded from graph, reported separately).</summary>
    public int TotalTestLoc { get; }

    /// <summary>File count in test projects (excluded from graph).</summary>
    public int TestFileCount { get; }

    /// <summary>Count of all source-defined classes (prod only).</summary>
    public int ClassCount { get; }

    /// <summary>Count of all source-defined interfaces (prod only).</summary>
    public int InterfaceCount { get; }

    private FileDependencyGraph(
        List<string> files,
        List<HashSet<int>> adj,
        List<HashSet<int>> revAdj,
        List<int> loc,
        int totalProdLoc,
        int totalTestLoc,
        int testFileCount,
        int classCount,
        int interfaceCount)
    {
        Files = files;
        Adj = adj;
        RevAdj = revAdj;
        Loc = loc;
        TotalProdLoc = totalProdLoc;
        TotalTestLoc = totalTestLoc;
        TestFileCount = testFileCount;
        ClassCount = classCount;
        InterfaceCount = interfaceCount;
    }

    public static async Task<FileDependencyGraph> BuildAsync(Solution solution, CancellationToken ct)
    {
        var solutionDir = LocationHelper.GetSolutionDirectory(solution);

        // Pass 1 — collect all prod files, assign indices, and for each source-declared
        // symbol record which file its primary declaration lives in.
        var fileIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var files = new List<string>();
        var loc = new List<int>();
        var symbolToFile = new Dictionary<ISymbol, int>(SymbolEqualityComparer.Default);
        int classCount = 0;
        int interfaceCount = 0;
        int totalTestLoc = 0;
        int testFileCount = 0;

        // Track (project, tree, model) tuples for pass 2 reference scan.
        var prodTrees = new List<(Project Project, SyntaxTree Tree, SemanticModel Model)>();

        foreach (var project in solution.Projects)
        {
            if (ct.IsCancellationRequested) break;

            bool isTest = IsTestProject(project);

            var compilation = await project.GetCompilationAsync(ct);
            if (compilation is null) continue;

            foreach (var tree in compilation.SyntaxTrees)
            {
                var path = tree.FilePath ?? "";
                if (IsExcludedPath(path)) continue;

                var relativeLines = CountNonBlankLines(await tree.GetTextAsync(ct));

                if (isTest)
                {
                    totalTestLoc += relativeLines;
                    testFileCount++;
                    continue;
                }

                var normalized = LocationHelper.NormalizePath(path, solutionDir);

                // Multi-project solutions compile the same file twice when projects
                // reference each other; only register once.
                if (fileIndex.ContainsKey(normalized)) continue;

                int idx = files.Count;
                fileIndex[normalized] = idx;
                files.Add(normalized);
                loc.Add(relativeLines);

                var model = compilation.GetSemanticModel(tree);
                prodTrees.Add((project, tree, model));

                // Register all type declarations in this file so references
                // in pass 2 can be routed back to their declaring file.
                var root = await tree.GetRootAsync(ct);
                foreach (var typeDecl in root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
                {
                    var symbol = model.GetDeclaredSymbol(typeDecl, ct) as INamedTypeSymbol;
                    if (symbol is null) continue;
                    if (symbol.IsImplicitlyDeclared) continue;

                    // Use the ORIGINAL definition so generic instantiations resolve to the declaring file.
                    ISymbol original = symbol.OriginalDefinition;
                    symbolToFile.TryAdd(original, idx);

                    if (symbol.TypeKind == TypeKind.Class || symbol.TypeKind == TypeKind.Struct)
                        classCount++;
                    else if (symbol.TypeKind == TypeKind.Interface)
                        interfaceCount++;
                }
            }
        }

        int n = files.Count;
        var adj = new List<HashSet<int>>(n);
        var revAdj = new List<HashSet<int>>(n);
        for (int i = 0; i < n; i++)
        {
            adj.Add(new HashSet<int>());
            revAdj.Add(new HashSet<int>());
        }

        // Pass 2 — walk every identifier in every prod file, resolve to a symbol,
        // map the symbol's containing type back to its declaring file, add edge.
        foreach (var (project, tree, model) in prodTrees)
        {
            if (ct.IsCancellationRequested) break;

            var path = LocationHelper.NormalizePath(tree.FilePath ?? "", solutionDir);
            if (!fileIndex.TryGetValue(path, out var srcIdx)) continue;

            var root = await tree.GetRootAsync(ct);
            foreach (var node in root.DescendantNodes())
            {
                ISymbol? sym = null;
                if (node is IdentifierNameSyntax id)
                    sym = model.GetSymbolInfo(id, ct).Symbol;
                else if (node is GenericNameSyntax gen)
                    sym = model.GetSymbolInfo(gen, ct).Symbol;
                if (sym is null) continue;

                // Resolve to the containing named type — we care about file-to-file coupling.
                var target = sym switch
                {
                    INamedTypeSymbol nts => (ISymbol)nts.OriginalDefinition,
                    _ => sym.ContainingType?.OriginalDefinition
                };
                if (target is null) continue;

                if (symbolToFile.TryGetValue(target, out var dstIdx) && dstIdx != srcIdx)
                {
                    if (adj[srcIdx].Add(dstIdx))
                        revAdj[dstIdx].Add(srcIdx);
                }
            }
        }

        int totalProdLoc = 0;
        foreach (var l in loc) totalProdLoc += l;

        return new FileDependencyGraph(files, adj, revAdj, loc, totalProdLoc, totalTestLoc, testFileCount, classCount, interfaceCount);
    }

    private static bool IsTestProject(Project project)
    {
        if (project.Name.Contains("Test", StringComparison.OrdinalIgnoreCase)) return true;
        if (project.Name.Contains("Spec", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static bool IsExcludedPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return true;

        // obj/ — generated files (source generators, XAML, AssemblyInfo)
        if (path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
            path.Contains("/obj/"))
            return true;

        // Migrations/ — EF Core generated migration scaffolding, excluded from health metrics
        if (path.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}") ||
            path.Contains("/Migrations/"))
            return true;

        return false;
    }

    private static int CountNonBlankLines(Microsoft.CodeAnalysis.Text.SourceText text)
    {
        int count = 0;
        foreach (var line in text.Lines)
        {
            var span = line.ToString();
            for (int i = 0; i < span.Length; i++)
            {
                if (!char.IsWhiteSpace(span[i])) { count++; break; }
            }
        }
        return count;
    }
}
