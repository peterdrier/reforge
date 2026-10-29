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
/// <param name="CrossTypeCharge">
/// Cognitive points charged on top of <paramref name="Score"/>'s excess for single-caller methods
/// this call path reaches in other types: what the type boundary kept under the threshold, at most
/// one threshold.
/// </param>
/// <param name="CrossTypeTop">The cross-type callee with the largest call path, as <c>Type.Method</c>.</param>
public readonly record struct CallPathScore(
    int Score, int Own, int FoldedMethods, int FoldedLines, string? TopContributor,
    int CrossTypeCharge = 0, string? CrossTypeTop = null);

/// <summary>
/// Resolves each method's call-path complexity for a solution.
/// <para>
/// A method with exactly one caller is not a method — it is part of its caller, and
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
/// Four details are load-bearing:
/// <list type="bullet">
/// <item>Fold only into the <b>sole</b> caller. A helper with two callers is shared code and folding
/// it into both would count it twice; it also stops folding the moment it gains a second caller,
/// which is the reuse incentive.</item>
/// <item>Follow <b>invocations</b>, not method groups. A method handed to something else as a
/// delegate is a separate entry point — folding registered callbacks read a Roslyn analyzer's
/// six-line <c>Initialize</c> as a 129-line body.</item>
/// <item>Fold <b>regardless of accessibility</b>. Widening a helper must never lower a score; if
/// only private helpers folded, making one internal would take its complexity out of its caller's
/// reading for free. Complexity is therefore independent of visibility and surface pricing alone
/// orders it. Excluded is what the sole-caller premise does not hold for: overrides, whose callers
/// bind to the base member and so are not seen here. A virtual method is still folded, since a call
/// through its declaring type binds to it, or <c>protected virtual</c> would be the escape. An
/// interface implementation folds when it is the only implementation of every member it
/// implements, so a call through the interface can only reach it; otherwise implementing a
/// one-member interface would be the escape.</item>
/// <item>Fold only within one <b>top-level type</b>, which is where a private helper's caller
/// already had to be, so the boundary does not move with visibility. Folding across types reads a
/// composition root as the whole program: this repo's <c>CommandRegistry.Create</c>, which calls
/// each command's <c>Create</c> once, read CC 2502 over 234 methods.</item>
/// <item>Charge what a <b>type boundary hides</b>, up to one threshold. Moving a single-caller
/// helper into another type takes it out of the fold, and the threshold then forgives the caller
/// and the helper separately. See <see cref="ChargeCrossings"/>.</item>
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
        /// <summary>Interface member → the methods that implicitly implement it.</summary>
        public Dictionary<string, HashSet<string>> Implementations { get; } = new(StringComparer.Ordinal);

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
        public string Qualified = "";
        public bool Scored;      // an ordinary method the complexity pass can charge
        public bool Foldable;    // every call to it binds to this symbol
        public List<string>? Implements;  // interface members it implements
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
        foreach (var info in graph.Methods.Values)
            if (info.Implements is not null)
                info.Foldable &= info.Implements.All(i => graph.Implementations[i].Count == 1);
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
        // A sole caller in another type is recorded apart: it does not fold, it is charged.
        var soleCaller = new Dictionary<string, string>(StringComparer.Ordinal);
        var crossCaller = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, info) in graph.Methods)
        {
            if (!info.Foldable) continue;
            if (!graph.Callers.TryGetValue(key, out var cs) || cs.Count != 1 || cs.Contains(key)) continue;
            var callerKey = cs.First();
            if (graph.Methods.TryGetValue(callerKey, out var caller) && caller.Scored
                && graph.Callees.TryGetValue(callerKey, out var invoked) && invoked.Contains(key))
                (caller.TopType == info.TopType ? soleCaller : crossCaller)[key] = callerKey;
        }
        BreakCycles(soleCaller);

        var foldable = soleCaller.Keys.ToHashSet(StringComparer.Ordinal);
        var scores = Fold(graph, foldable);
        ChargeCrossings(graph, crossCaller, soleCaller, scores);
        return new CallPathFold(scores, foldable);
    }

    /// <summary>
    /// The cross-type half of the fold. A method whose one caller sits in another type stays a
    /// method of its own, and its caller's same-type group is charged what the boundary hid.
    /// <para>
    /// Each group's <i>extent</i> is its folded path plus the extents of its cross-type sole
    /// callees. The charge is the threshold's hinge over the extent, less the hinges its callees
    /// already pay on their own extents and the one it pays on its own path. Summed over a tree of
    /// groups those terms telescope to the hinge over the root's extent, which is exactly what the
    /// tree would be charged as one type. So moving helpers into other types, any number of hops
    /// deep, one per group, can never lower the cognitive charge.
    /// </para>
    /// <para>
    /// The charge is capped at one threshold per group. One crossing can hide at most one
    /// threshold, so the cap never binds on a single move. It binds on a dispatcher: a group with
    /// <i>n</i> cross-type sole callees hides up to <i>n−1</i> thresholds more, and charging those
    /// reads every composition root as the whole program again. A group that calls two or more
    /// single-caller methods in other types can therefore escape whatever its crossings hide beyond
    /// one threshold.
    /// </para>
    /// </summary>
    private static void ChargeCrossings(Graph graph, Dictionary<string, string> crossCaller,
        Dictionary<string, string> soleCaller, Dictionary<string, CallPathScore> scores)
    {
        const int t = SurfaceScoreEngine.CognitiveThreshold;
        var children = crossCaller
            .GroupBy(kv => GroupRoot(kv.Value, soleCaller), kv => kv.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        var extent = new Dictionary<string, int>(StringComparer.Ordinal);
        var path = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (root, callees) in children)
        {
            int own = scores[root].Score, below = 0, best = -1;
            string? top = null;
            foreach (var c in callees)
            {
                int e = Extent(c);
                below += Hinge(e);
                if (e > best) { best = e; top = graph.Methods[c].Qualified; }
            }
            int hidden = Hinge(Extent(root)) - Hinge(own) - below;
            if (hidden > 0) scores[root] = scores[root] with { CrossTypeCharge = Math.Min(t, hidden), CrossTypeTop = top };
        }

        static int Hinge(int cc) => Math.Max(0, cc - t);

        // A cross-type cycle counts each member's path once.
        int Extent(string key)
        {
            if (extent.TryGetValue(key, out var cached)) return cached;
            int sum = scores[key].Score;
            if (!path.Add(key)) return sum;
            if (children.TryGetValue(key, out var cs))
                foreach (var c in cs) sum += Extent(c);
            path.Remove(key);
            extent[key] = sum;
            return sum;
        }
    }

    /// <summary>The method a same-type fold bills <paramref name="key"/> to.</summary>
    private static string GroupRoot(string key, Dictionary<string, string> soleCaller)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (soleCaller.TryGetValue(key, out var next) && seen.Add(key)) key = next;
        return key;
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
        var implementing = new Dictionary<INamedTypeSymbol, ILookup<ISymbol, ISymbol>>(SymbolEqualityComparer.Default);
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
                    Qualified = $"{m.ContainingType.Name}.{m.Name}",
                    TopType = Key(TopLevelType(m.ContainingType)),
                    Scored = ordinary && classOrStruct,
                    Foldable = ordinary && classOrStruct && !m.IsAbstract && !m.IsOverride,
                };
                var implemented = InterfaceMembersImplementedBy(m, implementing);
                if (implemented.Count > 0)
                {
                    info.Implements = implemented;
                    foreach (var i in implemented) graph.Add(graph.Implementations, i, Key(m));
                }
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

    /// <summary>The keys of the interface members <paramref name="m"/> implicitly implements.</summary>
    private static List<string> InterfaceMembersImplementedBy(IMethodSymbol m,
        Dictionary<INamedTypeSymbol, ILookup<ISymbol, ISymbol>> implementing)
    {
        if (m.DeclaredAccessibility != Accessibility.Public) return [];
        var type = m.ContainingType;
        if (!implementing.TryGetValue(type, out var lookup))
        {
            lookup = type.AllInterfaces.SelectMany(i => i.GetMembers())
                .Select(member => (Member: member, Impl: type.FindImplementationForInterfaceMember(member)))
                .Where(p => p.Impl is IMethodSymbol)
                .ToLookup(p => p.Impl!.OriginalDefinition, p => p.Member.OriginalDefinition,
                    SymbolEqualityComparer.Default);
            implementing[type] = lookup;
        }
        return lookup[m].Select(Key).Distinct(StringComparer.Ordinal).ToList();
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
        // A call through an interface member with one implementation can only land there.
        if (graph.Implementations.TryGetValue(key, out var impls) && impls.Count == 1) key = impls.First();
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
