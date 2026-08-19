using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;

namespace Reforge;

/// <summary>One production document, with the two things every syntax-walking command needs from it.</summary>
public readonly record struct SolutionDocument(
    Project Project,
    Document Document,
    SyntaxNode Root,
    SemanticModel Model);

/// <summary>
/// The walk every syntax-level command starts with: production projects, their documents, and each
/// document's root and semantic model.
/// </summary>
/// <remarks>
/// <para>
/// Extracted because it was written out longhand in every audit command — iterate projects, skip
/// anything named like a test, get the compilation, iterate documents, get root and model, null-guard
/// both, then descend. Issue #31 named this as the real duplication in this codebase: the top-scoring
/// symbols were, structurally, copies of one loop with different innermost bodies, and no rule charged
/// for it because the copies share a <i>shape</i> rather than lines.
/// </para>
/// <para>
/// A command that needs different filtering should not contort itself to use this. The analyzers
/// deliberately do not: <c>FileDependencyGraph</c> counts test LOC separately, <c>SnapshotAnalyzer</c>
/// excludes generated paths, <c>BuildInspector</c> wants diagnostics rather than syntax. Sharing a
/// preamble is worth it where the preamble is genuinely the same, and not otherwise.
/// </para>
/// </remarks>
public static class SolutionWalker
{
    /// <summary>
    /// Every document of every production project, with its root and semantic model. Documents whose
    /// root or model cannot be obtained are skipped, as each call site did individually.
    /// </summary>
    public static async IAsyncEnumerable<SolutionDocument> ProductionDocumentsAsync(
        Solution solution, [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var project in solution.Projects)
        {
            if (ct.IsCancellationRequested) yield break;
            if (IsTestProject(project)) continue;

            // Realizing the compilation is what makes the per-document semantic models cheap; it is
            // also the guard every call site used to write, so it stays.
            var compilation = await project.GetCompilationAsync(ct);
            if (compilation is null) continue;

            foreach (var document in project.Documents)
            {
                if (ct.IsCancellationRequested) yield break;

                var root = await document.GetSyntaxRootAsync(ct);
                var model = await document.GetSemanticModelAsync(ct);
                if (root is null || model is null) continue;

                yield return new SolutionDocument(project, document, root, model);
            }
        }
    }

    /// <summary>
    /// A project excluded from production analysis. Name-based, matching every call site this
    /// replaced — <c>SolutionClassifier</c> and the metrics passes use the same test.
    /// </summary>
    public static bool IsTestProject(Project project) =>
        project.Name.Contains("Test", StringComparison.OrdinalIgnoreCase);
}
