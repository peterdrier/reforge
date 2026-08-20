using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Reforge;

/// <summary>
/// One method's complexity measured over its <b>call path</b> rather than its declaration:
/// its own cognitive complexity plus that of every private single-caller helper it invokes,
/// transitively.
/// </summary>
/// <param name="Score">Cognitive complexity of the whole call path.</param>
/// <param name="Own">The part contributed by the method's own body.</param>
/// <param name="FoldedMethods">How many helpers were folded in.</param>
/// <param name="FoldedLines">Non-blank lines on the whole call path.</param>
/// <param name="TopContributor">
/// The folded helper contributing the most complexity, or null when the fold added none. Named in
/// the report so an agent is sent to the code rather than to the entry point.
/// </param>
public readonly record struct CallPathScore(
    int Score, int Own, int FoldedMethods, int FoldedLines, string? TopContributor);

/// <summary>
/// Resolves each method's call-path complexity for a solution.
/// <para>
/// A private method with exactly one caller is not a method — it is part of its caller, and
/// measuring it separately is what let a long method be split into single-caller parts for a lower
/// score while the code got worse. Folding removes the reward for splitting by volume: move a block
/// into a helper at the same nesting depth and the number does not move.
/// </para>
/// <para>
/// It does not make every split free of effect, because cognitive complexity charges
/// <c>1 + nestingDepth</c> per control structure and a helper's body is charged from its own root.
/// An extraction that takes a block <i>out of a nest</i> still pays, in proportion to the depth it
/// removes — which is the one split that genuinely reduces reading difficulty, and the one Sonar's
/// nesting penalty exists to reward. This file is its own worked example: as one four-deep loop nest
/// it read CC 104 for 89 points; the same logic as four methods at depth 2 reads CC 65 for 50. The
/// helpers fold straight back into it, so none of that came from the split — all 39 points came from
/// the two levels of nesting the split removed.
/// </para>
/// <para>
/// Two details are load-bearing and both were established by measuring the alternatives:
/// <list type="bullet">
/// <item>Fold only into the <b>sole</b> caller. A helper with two callers is shared code and folding
/// it into both would count it twice; it also stops folding the moment it gains a second caller,
/// which is the reuse incentive.</item>
/// <item>Follow <b>invocations</b>, not method groups. A method handed to something else as a
/// delegate is a separate entry point — folding registered callbacks read a Roslyn analyzer's
/// six-line <c>Initialize</c> as a 129-line body.</item>
/// </list>
/// </para>
/// </summary>
public static class CallPathComplexity
{
    /// <summary>The call graph as it is collected: one entry per declaration, plus both edge directions.</summary>
    private sealed class Graph
    {
        public Dictionary<string, MethodInfo> Methods { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, HashSet<string>> Callers { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, HashSet<string>> Callees { get; } = new(StringComparer.Ordinal);

        public void Add(Dictionary<string, HashSet<string>> edges, string from, string to)
        {
            if (!edges.TryGetValue(from, out var set)) edges[from] = set = new HashSet<string>(StringComparer.Ordinal);
            set.Add(to);
        }
    }

    private sealed class MethodInfo
    {
        public int Cognitive;
        public int Lines;
        public string Name = "";
        public bool Scored;      // an ordinary method the complexity pass can charge
        public bool Private;
    }

    /// <summary>
    /// The fold for one solution. <paramref name="analyzedAssemblies"/> is the classifier's admitted
    /// set, which is also how test projects stay out: a private helper is only reachable from its own
    /// type, so no caller can live in a project this skips.
    /// </summary>
    public static async Task<CallPathFold> BuildAsync(Solution solution,
        HashSet<string> analyzedAssemblies, CancellationToken ct)
    {
        var graph = new Graph();
        foreach (var project in solution.Projects)
        {
            ct.ThrowIfCancellationRequested();
            if (project.AssemblyName is null || !analyzedAssemblies.Contains(project.AssemblyName)) continue;
            var compilation = await project.GetCompilationAsync(ct);
            if (compilation is not null) await CollectProjectAsync(compilation, graph, ct);
        }

        // Foldable: private, exactly one caller, not itself, and the caller is a method the
        // complexity pass can charge. Without that last condition a helper called only from a
        // constructor or a property accessor would fold into something that is never scored and
        // so drop out of the report entirely.
        var foldable = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (key, info) in graph.Methods)
        {
            if (!info.Private) continue;
            if (!graph.Callers.TryGetValue(key, out var cs) || cs.Count != 1 || cs.Contains(key)) continue;
            if (graph.Methods.TryGetValue(cs.First(), out var caller) && caller.Scored) foldable.Add(key);
        }

        return new CallPathFold(Fold(graph, foldable), foldable);
    }

    /// <summary>
    /// Declarations first: the private names collected here are the only ones worth resolving in the
    /// reference walk, which is what keeps this pass affordable — most invocations in a project
    /// target framework or cross-type members that can never fold.
    /// </summary>
    private static async Task CollectProjectAsync(Compilation compilation, Graph graph, CancellationToken ct)
    {
        var privateNames = new HashSet<string>(StringComparer.Ordinal);
        var trees = new List<(SyntaxNode Root, SemanticModel Model)>();
        foreach (var tree in compilation.SyntaxTrees)
        {
            if (GeneratedCode.IsGeneratedFile(tree.FilePath)) continue;
            var model = compilation.GetSemanticModel(tree);
            var root = await tree.GetRootAsync(ct);
            trees.Add((root, model));
            CollectDeclarations(root, model, graph, privateNames, ct);
        }
        if (privateNames.Count == 0) return;

        foreach (var (root, model) in trees)
        {
            ct.ThrowIfCancellationRequested();
            CollectEdges(root, model, graph, privateNames, ct);
        }
    }

    private static void CollectDeclarations(SyntaxNode root, SemanticModel model, Graph graph,
        HashSet<string> privateNames, CancellationToken ct)
    {
        foreach (var decl in root.DescendantNodes().OfType<BaseMethodDeclarationSyntax>())
        {
            if (model.GetDeclaredSymbol(decl, ct) is not IMethodSymbol m) continue;
            var info = new MethodInfo
            {
                Cognitive = ImplementationComplexity.CognitiveDetail(decl).Score,
                Lines = ImplementationComplexity.NonBlankLines(decl),
                Name = m.Name,
                Scored = m.MethodKind == MethodKind.Ordinary
                    && m.AssociatedSymbol is null && !m.IsImplicitlyDeclared,
                Private = m.DeclaredAccessibility == Accessibility.Private && !m.IsAbstract
            };
            graph.Methods[Key(m)] = info;
            if (info.Private) privateNames.Add(m.Name);
        }
    }

    private static void CollectEdges(SyntaxNode root, SemanticModel model, Graph graph,
        HashSet<string> privateNames, CancellationToken ct)
    {
        foreach (var node in root.DescendantNodes())
        {
            if (node is not SimpleNameSyntax name) continue;
            if (!privateNames.Contains(name.Identifier.ValueText)) continue;
            var callee = FoldableCallee(name, model, graph, ct);
            if (callee is null) continue;
            var enclosing = EnclosingMethodKey(name, model, ct);
            if (enclosing is null) continue;

            graph.Add(graph.Callers, callee, enclosing);
            if (IsInvoked(name)) graph.Add(graph.Callees, enclosing, callee);
        }
    }

    /// <summary>The private method this name refers to, or null when it is not one.</summary>
    private static string? FoldableCallee(SimpleNameSyntax name, SemanticModel model, Graph graph,
        CancellationToken ct)
    {
        if (name.Parent is MemberAccessExpressionSyntax ma && ma.Name != name) return null;
        if (name.Parent is BaseMethodDeclarationSyntax) return null;
        if (model.GetSymbolInfo(name, ct).Symbol is not IMethodSymbol called) return null;
        var key = Key(called.OriginalDefinition);
        return graph.Methods.TryGetValue(key, out var target) && target.Private ? key : null;
    }

    private static Dictionary<string, CallPathScore> Fold(Graph graph, HashSet<string> foldable)
    {
        var methods = graph.Methods;
        var memo = new Dictionary<string, CallPathScore>(StringComparer.Ordinal);
        foreach (var key in methods.Keys) Eff(key, new HashSet<string>(StringComparer.Ordinal));
        return memo;

        CallPathScore Eff(string key, HashSet<string> path)
        {
            if (memo.TryGetValue(key, out var cached)) return cached;
            var info = methods[key];
            var self = new CallPathScore(info.Cognitive, info.Cognitive, 0, info.Lines, null);
            // A cycle charges the body only. Not memoized: the same method reached outside the
            // cycle must still fold.
            if (!path.Add(key)) return self;

            int score = info.Cognitive, lines = info.Lines, folded = 0, best = 0;
            string? top = null;
            if (graph.Callees.TryGetValue(key, out var cs))
            {
                foreach (var c in cs)
                {
                    if (c == key || !foldable.Contains(c)) continue;
                    if (!graph.Callers.TryGetValue(c, out var cc) || cc.Count != 1 || !cc.Contains(key)) continue;
                    var sub = Eff(c, path);
                    score += sub.Score;
                    lines += sub.FoldedLines;
                    folded += 1 + sub.FoldedMethods;
                    if (sub.Score > best) { best = sub.Score; top = methods[c].Name; }
                }
            }
            path.Remove(key);
            var result = new CallPathScore(score, info.Cognitive, folded, lines, best > 0 ? top : null);
            memo[key] = result;
            return result;
        }
    }

    /// <summary>
    /// The method a reference sits inside. Local functions are stepped over on purpose: a local
    /// function's complexity is already part of its enclosing member's reading, so a call it makes
    /// belongs to that member.
    /// </summary>
    private static string? EnclosingMethodKey(SyntaxNode node, SemanticModel model, CancellationToken ct)
    {
        for (var n = node.Parent; n is not null; n = n.Parent)
        {
            if (n is not MemberDeclarationSyntax) continue;
            var sym = model.GetDeclaredSymbol(n, ct);
            return sym is null ? null : Key(sym);
        }
        return null;
    }

    /// <summary>Whether the name is the target of a call, as opposed to a method group handed onward.</summary>
    private static bool IsInvoked(SimpleNameSyntax name) =>
        name.Parent is InvocationExpressionSyntax inv && inv.Expression == name
        || name.Parent is MemberAccessExpressionSyntax ma && ma.Name == name
           && ma.Parent is InvocationExpressionSyntax mi && mi.Expression == ma
        || name.Parent is MemberBindingExpressionSyntax mb && mb.Parent is InvocationExpressionSyntax;

    /// <summary>
    /// Assembly-qualified, so two projects' identically named private helpers never share an entry.
    /// </summary>
    public static string Key(ISymbol s) => $"{s.ContainingAssembly?.Name}|{s.ToDisplayString()}";
}

/// <summary>
/// The fold for one solution: per-method call-path complexity, and the helpers whose complexity was
/// billed to a caller. A folded-away helper must not also be charged on its own declaration.
/// </summary>
public sealed class CallPathFold(Dictionary<string, CallPathScore> scores, HashSet<string> foldedAway)
{
    public static CallPathFold Empty { get; } =
        new(new Dictionary<string, CallPathScore>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal));

    public bool WasFoldedAway(IMethodSymbol m) => foldedAway.Contains(CallPathComplexity.Key(m));

    /// <summary>
    /// The call-path reading for a method, or the <paramref name="ownScore"/> as-is when the method
    /// is not in the graph (its project was skipped, or its declaration is generated).
    /// </summary>
    public CallPathScore For(IMethodSymbol m, int ownScore, int ownLines)
        => scores.TryGetValue(CallPathComplexity.Key(m), out var s)
            ? s
            : new CallPathScore(ownScore, ownScore, 0, ownLines, null);
}
