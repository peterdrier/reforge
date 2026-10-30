using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Reforge;

/// <summary>
/// One method's complexity measured over its <b>call path</b> rather than its declaration:
/// its own cognitive complexity plus that of every single-caller helper it invokes, transitively.
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
/// Resolves each method's call-path complexity for a solution: what it costs to read an entry
/// point, helpers included. A reporting view only; the contents census charges every declaration
/// once from its own root, and names this figure in its detail so a reader is sent to the helper
/// carrying the weight rather than to a six-line entry point.
/// <para>
/// Four details are load-bearing:
/// <list type="bullet">
/// <item>Fold only into the <b>sole</b> caller. A helper with two callers is shared code and folding
/// it into both would count it twice; it also stops folding the moment it gains a second caller,
/// which is the reuse incentive.</item>
/// <item>Follow <b>invocations</b>, not method groups. A method handed to something else as a
/// delegate is a separate entry point — folding registered callbacks read a Roslyn analyzer's
/// six-line <c>Initialize</c> as a 129-line body.</item>
/// <item>Fold <b>regardless of accessibility</b>, so widening a helper does not change what its
/// caller is reported to read. Excluded is what the sole-caller premise does not hold for: overrides and interface
/// implementations, whose callers bind to the base or interface member and so are not seen here.
/// A virtual method is still folded, since a call through its declaring type binds to it, or
/// <c>protected virtual</c> would be the escape.</item>
/// <item>Fold only within one <b>top-level type</b>, which is where a private helper's caller
/// already had to be, so the boundary does not move with visibility. Folding across types reads a
/// composition root as the whole program: this repo's <c>CommandRegistry.Create</c>, which calls
/// each command's <c>Create</c> once, read CC 2502 over 234 methods.</item>
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
        public bool Foldable;    // every call to it binds to this symbol
        public string TopType = "";
    }

    /// <summary>
    /// The fold for one solution. <paramref name="analyzedAssemblies"/> is the classifier's admitted
    /// set, which is also how test projects stay out. A call from a skipped project is not counted:
    /// a test exercising a helper is not a second production caller, and a helper that folds when it
    /// should not is still charged, in its caller, so the error cannot lower a score.
    /// </summary>
    public static async Task<CallPathFold> BuildAsync(Solution solution,
        HashSet<string> analyzedAssemblies, CancellationToken ct)
    {
        // Declarations from every project before edges in any: a non-private helper's second
        // caller can live in another assembly, and a second caller anywhere makes it shared code.
        var graph = new Graph();
        var names = new HashSet<string>(StringComparer.Ordinal);
        var trees = new List<(SyntaxNode Root, SemanticModel Model)>();
        foreach (var project in solution.Projects)
        {
            ct.ThrowIfCancellationRequested();
            if (project.AssemblyName is null || !analyzedAssemblies.Contains(project.AssemblyName)) continue;
            var compilation = await project.GetCompilationAsync(ct);
            if (compilation is not null) await CollectDeclarationsAsync(compilation, graph, names, trees, ct);
        }
        if (names.Count > 0)
        {
            foreach (var (root, model) in trees)
            {
                ct.ThrowIfCancellationRequested();
                CollectEdges(root, model, graph, names, ct);
            }
        }

        // Foldable: exactly one caller, not itself, invoked by it rather than handed onward as a
        // delegate, and the caller is a method the complexity pass can charge. Each condition keeps
        // a helper from being folded away into something that never bills it — a constructor, a
        // property accessor, or a caller that only registers it — and so dropping out of the report.
        var soleCaller = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, info) in graph.Methods)
        {
            if (!info.Foldable) continue;
            if (!graph.Callers.TryGetValue(key, out var cs) || cs.Count != 1 || cs.Contains(key)) continue;
            var callerKey = cs.First();
            if (graph.Methods.TryGetValue(callerKey, out var caller) && caller.Scored
                && caller.TopType == info.TopType
                && graph.Callees.TryGetValue(callerKey, out var invoked) && invoked.Contains(key))
                soleCaller[key] = callerKey;
        }
        BreakCycles(soleCaller);

        var foldable = soleCaller.Keys.ToHashSet(StringComparer.Ordinal);
        return new CallPathFold(Fold(graph, foldable));
    }

    /// <summary>
    /// Methods that are each other's sole caller (<c>Render</c> calls <c>RenderChildren</c>, which
    /// calls <c>Render</c>) would all fold away with no root left to bill them. Each such cycle
    /// keeps one member, the ordinally first, as its root.
    /// </summary>
    private static void BreakCycles(Dictionary<string, string> soleCaller)
    {
        foreach (var start in soleCaller.Keys.Order(StringComparer.Ordinal).ToList())
        {
            var walk = new List<string>();
            var cur = start;
            while (soleCaller.TryGetValue(cur, out var next) && !walk.Contains(cur))
            {
                walk.Add(cur);
                cur = next;
            }
            int at = walk.IndexOf(cur);
            if (at >= 0) soleCaller.Remove(walk.Skip(at).Min(StringComparer.Ordinal)!);
        }
    }

    /// <summary>
    /// The foldable names collected here are the only ones worth resolving in the reference walk,
    /// which is what keeps this pass affordable — most invocations target framework members that
    /// can never fold.
    /// </summary>
    private static async Task CollectDeclarationsAsync(Compilation compilation, Graph graph,
        HashSet<string> names, List<(SyntaxNode Root, SemanticModel Model)> trees, CancellationToken ct)
    {
        var implementing = new Dictionary<INamedTypeSymbol, HashSet<ISymbol>>(SymbolEqualityComparer.Default);
        foreach (var tree in compilation.SyntaxTrees)
        {
            if (GeneratedCode.IsGeneratedFile(tree.FilePath)) continue;
            var model = compilation.GetSemanticModel(tree);
            var root = await tree.GetRootAsync(ct);
            trees.Add((root, model));

            foreach (var decl in root.DescendantNodes().OfType<BaseMethodDeclarationSyntax>())
            {
                if (model.GetDeclaredSymbol(decl, ct) is not IMethodSymbol m) continue;
                bool ordinary = m.MethodKind == MethodKind.Ordinary
                    && m.AssociatedSymbol is null && !m.IsImplicitlyDeclared;
                bool classOrStruct = m.ContainingType.TypeKind is TypeKind.Class or TypeKind.Struct;
                var info = new MethodInfo
                {
                    Cognitive = ImplementationComplexity.CognitiveDetail(decl).Score,
                    Lines = ImplementationComplexity.NonBlankLines(decl),
                    Name = m.Name,
                    TopType = Key(TopLevelType(m.ContainingType)),
                    Scored = ordinary && classOrStruct,
                    Foldable = ordinary && classOrStruct && !m.IsAbstract && !m.IsOverride
                        && !ImplementsInterface(m, implementing)
                };
                graph.Methods[Key(m)] = info;
                if (info.Foldable) names.Add(m.Name);
            }
        }
    }

    private static INamedTypeSymbol TopLevelType(INamedTypeSymbol type)
    {
        while (type.ContainingType is { } outer) type = outer;
        return type;
    }

    /// <summary>Whether <paramref name="m"/> implicitly implements an interface member.</summary>
    private static bool ImplementsInterface(IMethodSymbol m,
        Dictionary<INamedTypeSymbol, HashSet<ISymbol>> implementing)
    {
        if (m.DeclaredAccessibility != Accessibility.Public) return false;
        var type = m.ContainingType;
        if (!implementing.TryGetValue(type, out var set))
        {
            set = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
            foreach (var member in type.AllInterfaces.SelectMany(i => i.GetMembers()))
                if (type.FindImplementationForInterfaceMember(member) is IMethodSymbol impl)
                    set.Add(impl.OriginalDefinition);
            implementing[type] = set;
        }
        return set.Contains(m);
    }

    private static void CollectEdges(SyntaxNode root, SemanticModel model, Graph graph,
        HashSet<string> names, CancellationToken ct)
    {
        foreach (var node in root.DescendantNodes())
        {
            if (node is not SimpleNameSyntax name) continue;
            if (!names.Contains(name.Identifier.ValueText)) continue;
            var callee = FoldableCallee(name, model, graph, ct);
            if (callee is null) continue;
            var enclosing = EnclosingMethodKey(name, model, ct);
            if (enclosing is null) continue;

            graph.Add(graph.Callers, callee, enclosing);
            if (IsInvoked(name)) graph.Add(graph.Callees, enclosing, callee);
        }
    }

    /// <summary>The foldable method this name refers to, or null when it is not one.</summary>
    private static string? FoldableCallee(SimpleNameSyntax name, SemanticModel model, Graph graph,
        CancellationToken ct)
    {
        if (name.Parent is MemberAccessExpressionSyntax ma && ma.Name != name) return null;
        if (name.Parent is BaseMethodDeclarationSyntax) return null;
        if (model.GetSymbolInfo(name, ct).Symbol is not IMethodSymbol called) return null;
        var key = Key((called.ReducedFrom ?? called).OriginalDefinition);
        return graph.Methods.TryGetValue(key, out var target) && target.Foldable ? key : null;
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
    /// Assembly-qualified, so two projects' identically named helpers never share an entry.
    /// </summary>
    public static string Key(ISymbol s) => $"{s.ContainingAssembly?.Name}|{s.ToDisplayString()}";
}

/// <summary>
/// The fold for one solution: per-method call-path complexity. A reporting view only — what it
/// costs to read an entry point — and never a scored total; the census charges each declaration
/// once, from its own root.
/// </summary>
public sealed class CallPathFold(Dictionary<string, CallPathScore> scores)
{
    public static CallPathFold Empty { get; } = new(new Dictionary<string, CallPathScore>(StringComparer.Ordinal));

    /// <summary>
    /// The call-path reading for a method, or the <paramref name="ownScore"/> as-is when the method
    /// is not in the graph (its project was skipped, or its declaration is generated).
    /// </summary>
    public CallPathScore For(IMethodSymbol m, int ownScore, int ownLines)
        => scores.TryGetValue(CallPathComplexity.Key(m), out var s)
            ? s
            : new CallPathScore(ownScore, ownScore, 0, ownLines, null);
}
