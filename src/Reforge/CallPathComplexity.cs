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
/// score while the code got worse. Folding leaves the number unchanged under that split by
/// construction, so the only edits that reduce it are removing logic from the path or giving a
/// helper a second real caller.
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
        var methods = new Dictionary<string, MethodInfo>(StringComparer.Ordinal);
        var callers = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var callees = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (var project in solution.Projects)
        {
            ct.ThrowIfCancellationRequested();
            if (project.AssemblyName is null || !analyzedAssemblies.Contains(project.AssemblyName)) continue;
            var compilation = await project.GetCompilationAsync(ct);
            if (compilation is null) continue;

            // Declarations first: the private names collected here are the only ones worth
            // resolving in the reference walk, which is what keeps this pass affordable — most
            // invocations in a project target framework or cross-type members that can never fold.
            var privateNames = new HashSet<string>(StringComparer.Ordinal);
            var trees = new List<(SyntaxTree Tree, SemanticModel Model)>();
            foreach (var tree in compilation.SyntaxTrees)
            {
                if (GeneratedCode.IsGeneratedFile(tree.FilePath)) continue;
                var model = compilation.GetSemanticModel(tree);
                trees.Add((tree, model));
                foreach (var decl in (await tree.GetRootAsync(ct)).DescendantNodes().OfType<BaseMethodDeclarationSyntax>())
                {
                    if (model.GetDeclaredSymbol(decl) is not IMethodSymbol m) continue;
                    var info = new MethodInfo
                    {
                        Cognitive = ImplementationComplexity.CognitiveDetail(decl).Score,
                        Lines = ImplementationComplexity.NonBlankLines(decl),
                        Name = m.Name,
                        Scored = m.MethodKind == MethodKind.Ordinary
                            && m.AssociatedSymbol is null && !m.IsImplicitlyDeclared,
                        Private = m.DeclaredAccessibility == Accessibility.Private && !m.IsAbstract
                    };
                    methods[Key(m)] = info;
                    if (info.Private) privateNames.Add(m.Name);
                }
            }
            if (privateNames.Count == 0) continue;

            foreach (var (tree, model) in trees)
            {
                ct.ThrowIfCancellationRequested();
                foreach (var node in (await tree.GetRootAsync(ct)).DescendantNodes())
                {
                    if (node is not SimpleNameSyntax name) continue;
                    if (!privateNames.Contains(name.Identifier.ValueText)) continue;
                    if (name.Parent is MemberAccessExpressionSyntax ma && ma.Name != name) continue;
                    if (name.Parent is BaseMethodDeclarationSyntax) continue;
                    if (model.GetSymbolInfo(name, ct).Symbol is not IMethodSymbol called) continue;
                    var key = Key(called.OriginalDefinition);
                    if (!methods.TryGetValue(key, out var target) || !target.Private) continue;

                    var enclosing = EnclosingMethodKey(name, model, ct);
                    if (enclosing is null) continue;
                    if (!callers.TryGetValue(key, out var set))
                        callers[key] = set = new HashSet<string>(StringComparer.Ordinal);
                    set.Add(enclosing);
                    if (IsInvoked(name))
                    {
                        if (!callees.TryGetValue(enclosing, out var outs))
                            callees[enclosing] = outs = new HashSet<string>(StringComparer.Ordinal);
                        outs.Add(key);
                    }
                }
            }
        }

        // Foldable: private, exactly one caller, not itself, and the caller is a method the
        // complexity pass can charge. Without that last condition a helper called only from a
        // constructor or a property accessor would fold into something that is never scored and
        // so drop out of the report entirely.
        var foldable = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (key, info) in methods)
        {
            if (!info.Private) continue;
            if (!callers.TryGetValue(key, out var cs) || cs.Count != 1 || cs.Contains(key)) continue;
            var sole = cs.First();
            if (methods.TryGetValue(sole, out var caller) && caller.Scored) foldable.Add(key);
        }

        return new CallPathFold(Fold(methods, callees, callers, foldable), foldable);
    }

    private static Dictionary<string, CallPathScore> Fold(
        Dictionary<string, MethodInfo> methods,
        Dictionary<string, HashSet<string>> callees,
        Dictionary<string, HashSet<string>> callers,
        HashSet<string> foldable)
    {
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
            if (callees.TryGetValue(key, out var cs))
            {
                foreach (var c in cs)
                {
                    if (c == key || !foldable.Contains(c)) continue;
                    if (!callers.TryGetValue(c, out var cc) || cc.Count != 1 || !cc.Contains(key)) continue;
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
