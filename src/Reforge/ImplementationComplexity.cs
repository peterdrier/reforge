using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Reforge;

/// <summary>
/// Which scoring axis a rule belongs to. The surface axis (durable public surface,
/// dependency use, return shape) and the implementation-shape axis (implementation
/// cost of what hides behind that surface) are kept as <b>separate scalars</b> on
/// purpose. They are never summed into a single number that a refactoring loop is
/// allowed to optimize — that would let the loop trade surface for complexity and
/// still "improve" the net. The combined total is informational only; the real
/// signal is the per-axis Pareto comparison in <see cref="SurfaceScoreBaseline"/>.
/// </summary>
public static class SurfaceScoreRuleGroups
{
    public static readonly IReadOnlySet<string> ImplementationShape = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "largeClass",
        "methodComplexity",
        "methodCount",
        "actionDispatcher",
        "mutationModeParameter",
        "flagsControlFlow"
    };

    public static bool IsImplementationShape(string rule) => ImplementationShape.Contains(rule);
}

/// <summary>
/// A cognitive-complexity reading: the total, and how much of it sits inside a nested function
/// declared at the member's own top level (with that function's line, or 0 when there is none).
/// </summary>
public readonly record struct CognitiveScore(int Score, int NestedScore, int NestedLine)
{
    /// <summary>
    /// Whether the nested function holds most of the score, i.e. whether pointing a reader at the
    /// member's signature would point them away from the code. A strict majority, so a member with
    /// its complexity spread across several lambdas still reports against the member.
    /// </summary>
    public bool NestedDominates => NestedLine > 0 && NestedScore * 2 > Score;
}

/// <summary>
/// Syntax-level implementation-complexity analysis. All methods are pure functions
/// over Roslyn syntax/symbol input — no workspace state — so they're directly unit
/// testable. The engine calls these once per method/class and turns the raw counts
/// into points via the configured weights.
/// </summary>
public static class ImplementationComplexity
{
    // ---------------- Size signals ----------------

    /// <summary>Non-blank source lines spanned by a syntax node (method declaration, class declaration, …).</summary>
    public static int NonBlankLines(SyntaxNode node)
    {
        var text = node.GetText();
        int n = 0;
        foreach (var line in text.Lines)
        {
            var s = line.ToString();
            if (!string.IsNullOrWhiteSpace(s)) n++;
        }
        return n;
    }

    /// <summary>largeClass base points: +10 per 250 nonblank LOC over 750, +25 over 1500.</summary>
    public static int LargeClassPoints(int loc)
    {
        if (loc <= 750) return 0;
        int p = ((loc - 750) / 250) * 10;
        if (loc > 1500) p += 25;
        return p;
    }

    // ---------------- Cognitive complexity (SonarSource) ----------------

    /// <summary>
    /// SonarSource Cognitive Complexity over a method body. Folds nesting into branch
    /// cost (a branch N levels deep costs 1+N), treats <c>else</c>/<c>else if</c> as a
    /// flat +1 with no nesting penalty, and leaves a single early-return guard cheap
    /// (just +1, no nesting). One metric replaces separate branch-count and
    /// nesting-depth knobs and gives an established baseline that's harder to game.
    /// </summary>
    public static int Cognitive(BaseMethodDeclarationSyntax method) => CognitiveDetail(method).Score;

    /// <summary>
    /// <see cref="Cognitive"/> plus where the score actually sits: <see cref="CognitiveScore.NestedScore"/>
    /// is the share accrued inside the heaviest nested function declared at the member's own top
    /// level, and <see cref="CognitiveScore.NestedLine"/> is that function's line.
    /// </summary>
    /// <remarks>
    /// Reported because a member whose entire body is a delegate — an action handler, a
    /// <c>SetAction</c> callback, a registration lambda — carries all of its complexity in a node
    /// with no name and no declaration line of its own, and naming the enclosing method sends a
    /// reader to a signature the code is not in.
    /// </remarks>
    public static CognitiveScore CognitiveDetail(BaseMethodDeclarationSyntax method)
        => method.Body is { } block ? CognitiveOf(block)
            : method.ExpressionBody is { } arrow ? CognitiveOf(arrow)
            : new CognitiveScore(0, 0, 0);

    /// <summary>
    /// Cognitive complexity of one piece of code walked from its own root: a block body, an arrow
    /// body, or an initializer expression. What a method, an accessor and a delegate-valued field
    /// have in common, so all three are measured the same way.
    /// </summary>
    internal static CognitiveScore CognitiveOf(SyntaxNode code)
    {
        var w = new CognitiveWalker();
        switch (code)
        {
            case BlockSyntax block: w.VisitChildren(block, 0); break;
            case ArrowExpressionClauseSyntax arrow: w.Visit(arrow.Expression, 0); break;
            default: w.Visit(code, 0); break;
        }
        return new CognitiveScore(w.Score, w.TopNestedScore, w.TopNestedLine);
    }

    /// <summary>
    /// Cognitive complexity of <paramref name="code"/> walked as if it sat at <paramref name="nesting"/>,
    /// each node visited the way its parent visits it.
    /// </summary>
    internal static int CognitiveAt(IEnumerable<SyntaxNode> code, int nesting)
    {
        var w = new CognitiveWalker();
        foreach (var node in code) w.Visit(node, nesting);
        return w.Score;
    }

    private sealed class CognitiveWalker
    {
        public int Score;
        /// <summary>Points accrued inside the heaviest top-level nested function, and its line.</summary>
        public int TopNestedScore;
        public int TopNestedLine;
        /// <summary>
        /// Whether the walk is currently inside a nested function. Saved and restored around each
        /// one, so it reflects the path to the current node rather than how many have been seen.
        /// </summary>
        private bool _inNestedFunction;

        public void VisitChildren(SyntaxNode node, int nesting)
        {
            foreach (var child in node.ChildNodes())
                Visit(child, nesting);
        }

        public void Visit(SyntaxNode node, int nesting)
        {
            switch (node)
            {
                case IfStatementSyntax ifs:
                    VisitIf(ifs, nesting);
                    return;
                case ForStatementSyntax or ForEachStatementSyntax or ForEachVariableStatementSyntax
                     or WhileStatementSyntax or DoStatementSyntax:
                    Score += 1 + nesting;
                    CountLogical(GetLoopCondition(node));
                    VisitChildren(node, nesting + 1);
                    return;
                case SwitchStatementSyntax sw:
                    Score += 1 + nesting;
                    foreach (var section in sw.Sections)
                        foreach (var st in section.Statements)
                            Visit(st, nesting + 1);
                    return;
                case SwitchExpressionSyntax swe:
                    Score += 1 + nesting;
                    foreach (var arm in swe.Arms)
                        Visit(arm.Expression, nesting + 1);
                    return;
                case TryStatementSyntax t:
                    Visit(t.Block, nesting);
                    foreach (var c in t.Catches)
                    {
                        Score += 1 + nesting;
                        Visit(c.Block, nesting + 1);
                    }
                    if (t.Finally is { } f) Visit(f.Block, nesting);
                    return;
                case ConditionalExpressionSyntax cond:
                    Score += 1 + nesting;
                    CountLogical(cond.Condition);
                    Visit(cond.WhenTrue, nesting + 1);
                    Visit(cond.WhenFalse, nesting + 1);
                    return;
                case ParenthesizedLambdaExpressionSyntax or SimpleLambdaExpressionSyntax
                     or AnonymousMethodExpressionSyntax or LocalFunctionStatementSyntax:
                {
                    // A nested function increases the structural nesting of its body only when it
                    // sits inside enclosing structure of its own. At the member's own top level
                    // there is no increment-bearing node between it and its member, so the level
                    // would be charged for the shape of an API rather than for anything a reader
                    // has to hold: `command.SetAction(async (parse, ct) => { ... })` puts an entire
                    // member body one level down for no structural reason, and every branch inside
                    // it then costs 1 more than the same code written as a method body.
                    //
                    // "At the member's own top level" needs both halves. `nesting == 0` alone is
                    // not enough once the exemption is granted: the exempt body is walked at 0, so
                    // a lambda declared INSIDE it would see 0 as well and take the exemption a
                    // second time — a LINQ lambda inside a SetAction callback would then score its
                    // branches at member depth, though it is genuinely two functions deep. Tracking
                    // "already inside a nested function" separately from the nesting value keeps
                    // the exemption to the outermost one, while still granting it to each of
                    // several sibling top-level lambdas.
                    bool wasInNested = _inNestedFunction;
                    int inner = (nesting == 0 && !wasInNested) ? 0 : nesting + 1;
                    int before = Score;
                    _inNestedFunction = true;
                    VisitChildren(node, inner);
                    _inNestedFunction = wasInNested;
                    // Attribution (issue #31 3a): remember the heaviest nested function at the
                    // member's own top level, so a caller can point at the code instead of at a
                    // signature the complexity is not in.
                    if (nesting == 0 && !wasInNested && Score - before > TopNestedScore)
                    {
                        TopNestedScore = Score - before;
                        TopNestedLine = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                    }
                    return;
                }
            }
            VisitChildren(node, nesting);
        }

        private void VisitIf(IfStatementSyntax ifs, int nesting)
        {
            Score += 1 + nesting;
            CountLogical(ifs.Condition);
            VisitChildren(ifs.Statement, nesting + 1);
            VisitElse(ifs.Else, nesting);
        }

        private void VisitElse(ElseClauseSyntax? els, int nesting)
        {
            if (els is null) return;
            if (els.Statement is IfStatementSyntax elseIf)
            {
                Score += 1; // else-if: structural increment, no nesting penalty
                CountLogical(elseIf.Condition);
                VisitChildren(elseIf.Statement, nesting + 1);
                VisitElse(elseIf.Else, nesting);
            }
            else
            {
                Score += 1; // plain else
                VisitChildren(els.Statement, nesting + 1);
            }
        }

        private void CountLogical(SyntaxNode? condition)
        {
            if (condition is null) return;
            // +1 per maximal run of the same logical operator (&&/||). Switching operator
            // type starts a new run.
            SyntaxKind? prev = null;
            foreach (var bin in condition.DescendantNodesAndSelf().OfType<BinaryExpressionSyntax>())
            {
                var k = bin.Kind();
                if (k is SyntaxKind.LogicalAndExpression or SyntaxKind.LogicalOrExpression)
                {
                    if (prev != k) { Score += 1; prev = k; }
                }
            }
        }

        private static SyntaxNode? GetLoopCondition(SyntaxNode loop) => loop switch
        {
            ForStatementSyntax f => f.Condition,
            WhileStatementSyntax w => w.Condition,
            DoStatementSyntax d => d.Condition,
            _ => null
        };
    }

    // ---------------- Cyclomatic complexity ----------------

    /// <summary>
    /// McCabe cyclomatic complexity over a method body: one plus every independent branch
    /// (conditionals, loops, catch clauses, switch arms, and the short-circuiting operators).
    /// Flat where <see cref="Cognitive"/> is nesting-weighted, and the metric <c>snapshot</c>
    /// has always recorded — kept here so the history series and the per-section metrics are
    /// the same number computed once.
    /// </summary>
    public static int Cyclomatic(SyntaxNode methodBody)
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
                // CommonForEachStatementSyntax, not ForEachStatementSyntax: a deconstructing
                // `foreach (var (k, v) in xs)` parses as ForEachVariableStatementSyntax, a sibling
                // rather than a subtype, and matching only the latter silently undercounts it. The
                // cognitive walker has always handled both.
                CommonForEachStatementSyntax => 1,
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

    /// <summary>
    /// <see cref="Cyclomatic(SyntaxNode)"/> over a declaration's body — block or expression-bodied.
    /// Returns 0 for a bodyless declaration (abstract, interface, partial definition): those carry
    /// no implementation, and folding a 1 in for each would drag every distribution toward 1.
    /// </summary>
    public static int Cyclomatic(BaseMethodDeclarationSyntax method)
    {
        SyntaxNode? body = (SyntaxNode?)method.Body ?? method.ExpressionBody;
        return body is null ? 0 : Cyclomatic(body);
    }

    // ---------------- Behavioral read vs mutation ----------------

    // Only persistence-COMMIT calls are treated as definitive write signals. Add/Update/Remove
    // are deliberately excluded: without the semantic model we can't tell DbSet.Add from
    // List.Add, and query methods routinely build result lists with .Add() — counting those as
    // writes would strip the read exemption from ordinary queries. SaveChanges / ExecuteUpdate /
    // ExecuteDelete are unambiguous. Methods that mutate without committing in-body are still
    // caught by the command-shape heuristic (no data returned, non-query name).
    private static readonly string[] WriteCallNames =
    {
        "SaveChanges", "SaveChangesAsync",
        "ExecuteUpdate", "ExecuteUpdateAsync", "ExecuteDelete", "ExecuteDeleteAsync"
    };

    private static readonly string[] QueryVerbs =
    {
        "Get", "Find", "Query", "List", "Search", "Read", "Load", "Fetch", "Count", "Exists", "Lookup", "Has"
    };

    /// <summary>
    /// True when the method observably mutates persistent state. Decided behaviorally,
    /// not by the parameter's type name: a body that calls SaveChanges / DbSet
    /// Add-Update-Remove, or a command-shaped method that returns no data, is a
    /// mutation; a query-verb method that returns data and writes nothing is a read.
    /// This is what makes the read-shape exemption rename-proof — calling a parameter
    /// <c>FooQuery</c> instead of <c>FooAction</c> changes nothing.
    /// </summary>
    public static bool IsMutation(IMethodSymbol method, BaseMethodDeclarationSyntax? syntax)
    {
        if (syntax is not null && CommitsPersistentWrite((SyntaxNode?)syntax.Body ?? syntax.ExpressionBody?.Expression))
            return true;

        bool returnsData = ReturnsData(method);
        var bare = StripAsync(method.Name);
        if (returnsData && QueryVerbs.Any(v => bare.StartsWith(v, StringComparison.Ordinal)))
            return false; // clearly a read
        if (!returnsData)
            return true; // command shape (void / non-generic Task) with no read signal
        return false; // returns data, no write calls -> treat as read
    }

    /// <summary>
    /// Whether a body contains a persistence-commit call — the one <b>definitive</b> write signal, as
    /// opposed to the command-shape heuristic. Exposed separately because not every write-capable
    /// member is a <c>BaseMethodDeclarationSyntax</c>: a property getter's declaration is an
    /// <c>AccessorDeclarationSyntax</c> or an arrow clause on the property itself, and the shape
    /// heuristic is meaningless for an accessor anyway (a getter returns data by definition).
    /// </summary>
    public static bool CommitsPersistentWrite(SyntaxNode? body)
    {
        if (body is null) return false;
        foreach (var inv in body.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>())
        {
            var name = InvokedName(inv);
            if (name is not null && WriteCallNames.Contains(name, StringComparer.Ordinal))
                return true;
        }
        return false;
    }

    public static bool IsRead(IMethodSymbol method, BaseMethodDeclarationSyntax? syntax)
        => !IsMutation(method, syntax);

    private static bool ReturnsData(IMethodSymbol m)
    {
        var t = m.ReturnType;
        if (t.SpecialType == SpecialType.System_Void) return false;
        if (t is INamedTypeSymbol n)
        {
            var od = n.OriginalDefinition.ToDisplayString();
            if (od is "System.Threading.Tasks.Task" or "System.Threading.Tasks.ValueTask") return false;
            if (od is "System.Threading.Tasks.Task<TResult>" or "System.Threading.Tasks.ValueTask<TResult>")
                return n.TypeArguments[0].SpecialType != SpecialType.System_Void;
        }
        return true;
    }

    private static string StripAsync(string name)
        => name.EndsWith("Async", StringComparison.Ordinal) && name.Length > 5
            ? name[..^5]
            : name;

    // ---------------- Selector dispatch (hidden operations) ----------------

    /// <summary>
    /// A selector branch's verdict. <see cref="ArmCount"/> is how many arms are operations of their
    /// own; <see cref="InlineArms"/> how many of those hold their work in place rather than calling
    /// a method of their own; <see cref="NestingRelief"/> how much cognitive complexity the arms
    /// carry only because they sit inside the branch (what they would shed as methods).
    /// </summary>
    public readonly record struct DispatcherAnalysis(bool Fires, int ArmCount, int InlineArms = 0, int NestingRelief = 0);

    private static readonly HashSet<string> DispatchParamNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "action", "mode", "operation", "command", "scope", "flags", "options", "kind", "type"
    };

    private static readonly string[] DispatchTypeSuffixes = { "Action", "Mode", "Operation", "Scope", "Flags", "Kind" };

    private sealed record Arm(IReadOnlyList<SyntaxNode> Code, int Nesting);

    /// <summary>
    /// Finds the operations a method hides behind a selector parameter: a switch, switch expression or
    /// if/else chain on an enum, a bool, or a parameter named or typed like a mode. The arms are
    /// judged by what they do, never by how they are written, so arms pasted inline and arms that
    /// call one private method each read the same.
    /// <para>An arm is an operation of its own when it calls a solution method no other arm calls,
    /// touches state of the method's type that no other arm touches, or writes that state (or commits
    /// a persistent write). Arms that only project values the method computed before branching, or
    /// that differ only by LINQ or BCL calls, share their work and are not separate operations. An
    /// <c>if</c> on the selector whose body always leaves the method has the rest of the block as its
    /// other arm.</para>
    /// </summary>
    public static DispatcherAnalysis AnalyzeDispatcher(IMethodSymbol method, BaseMethodDeclarationSyntax syntax,
        SemanticModel model)
    {
        SyntaxNode? body = (SyntaxNode?)syntax.Body ?? syntax.ExpressionBody?.Expression;
        if (body is null) return default;

        var paramNames = method.Parameters.Where(IsSelectorCandidate).Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        if (paramNames.Count == 0) return default;

        DispatcherAnalysis best = default;
        foreach (var sw in body.DescendantNodesAndSelf().OfType<SwitchStatementSyntax>())
        {
            if (!ReferencesParam(sw.Expression, paramNames)) continue;
            int n = NestingOf(sw, body) + 1;
            best = Better(best, Evaluate(sw.Sections.Select(s => new Arm(s.Statements.ToList<SyntaxNode>(), n)).ToList(), method, model));
        }

        foreach (var swe in body.DescendantNodesAndSelf().OfType<SwitchExpressionSyntax>())
        {
            if (!ReferencesParam(swe.GoverningExpression, paramNames)) continue;
            int n = NestingOf(swe, body) + 1;
            best = Better(best, Evaluate(swe.Arms.Select(a => new Arm(new[] { a.Expression }, n)).ToList(), method, model));
        }

        foreach (var ifs in body.DescendantNodesAndSelf().OfType<IfStatementSyntax>())
        {
            if (ifs.Parent is ElseClauseSyntax) continue; // only chain heads
            if (!ReferencesParam(ifs.Condition, paramNames)) continue;

            int n = NestingOf(ifs, body);
            var arms = new List<Arm>();
            IfStatementSyntax? cur = ifs;
            while (cur is not null)
            {
                if (!ReferencesParam(cur.Condition, paramNames)) break;
                arms.Add(new Arm(cur.Statement.ChildNodes().ToList(), n + 1));
                if (cur.Else?.Statement is IfStatementSyntax next) { cur = next; continue; }
                if (cur.Else is { } tail) arms.Add(new Arm(tail.Statement.ChildNodes().ToList(), n + 1));
                else if (Exits(cur.Statement) && ifs.Parent is BlockSyntax block)
                    arms.Add(new Arm(block.Statements.SkipWhile(s => s != ifs).Skip(1).ToList<SyntaxNode>(), n));
                cur = null;
            }
            best = Better(best, Evaluate(arms, method, model));
        }

        return best;
    }

    private static DispatcherAnalysis Better(DispatcherAnalysis a, DispatcherAnalysis b)
        => b.ArmCount > a.ArmCount ? b : a;

    private static DispatcherAnalysis Evaluate(List<Arm> arms, IMethodSymbol method, SemanticModel model)
    {
        if (arms.Count < 2) return default;
        var facts = arms.Select(a => ArmFacts.Of(a.Code, method.ContainingType, model)).ToList();
        var calls = new Dictionary<IMethodSymbol, int>(SymbolEqualityComparer.Default);
        var touches = new Dictionary<ISymbol, int>(SymbolEqualityComparer.Default);
        foreach (var f in facts)
        {
            foreach (var m in f.Calls) calls[m] = calls.GetValueOrDefault(m) + 1;
            foreach (var s in f.State) touches[s] = touches.GetValueOrDefault(s) + 1;
        }

        int own = 0, inline = 0, relief = 0;
        for (int i = 0; i < arms.Count; i++)
        {
            var f = facts[i];
            bool delegated = f.Calls.Any(m => calls[m] == 1);
            if (!delegated && !f.Writes && !f.State.Any(s => touches[s] == 1)) continue;
            own++;
            if (!delegated) inline++;
            relief += CognitiveAt(arms[i].Code, arms[i].Nesting) - CognitiveAt(arms[i].Code, 0);
        }
        return own >= 2 ? new DispatcherAnalysis(true, own, inline, relief) : default;
    }

    /// <summary>What one arm calls and which state of the method's own type it reads or writes.</summary>
    private sealed record ArmFacts(HashSet<IMethodSymbol> Calls, HashSet<ISymbol> State, bool Writes)
    {
        public static ArmFacts Of(IReadOnlyList<SyntaxNode> code, INamedTypeSymbol owner, SemanticModel model)
        {
            var calls = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
            var state = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
            bool writes = false;
            foreach (var node in code.SelectMany(c => c.DescendantNodesAndSelf()))
            {
                if (node is InvocationExpressionSyntax inv && SolutionMethod(inv, model) is { } m) calls.Add(m);
                if (node is not SimpleNameSyntax name || !IsOwnState(model.GetSymbolInfo(name).Symbol, owner, out var s)) continue;
                state.Add(s);
                writes |= IsWriteTarget(name);
            }
            writes |= code.Any(CommitsPersistentWrite);
            return new ArmFacts(calls, state, writes);
        }

        private static bool IsOwnState(ISymbol? symbol, INamedTypeSymbol owner, out ISymbol state)
        {
            state = symbol?.OriginalDefinition!;
            if (symbol is IFieldSymbol { IsConst: true }) return false;
            if (symbol is not (IFieldSymbol or IPropertySymbol or IEventSymbol)) return false;
            for (INamedTypeSymbol? t = owner.OriginalDefinition; t is not null; t = t.BaseType?.OriginalDefinition)
                if (SymbolEqualityComparer.Default.Equals(t, symbol.ContainingType.OriginalDefinition)) return true;
            return false;
        }

        private static bool IsWriteTarget(SimpleNameSyntax name)
        {
            SyntaxNode target = name.Parent is MemberAccessExpressionSyntax ma && ma.Name == name ? ma : name;
            return target.Parent switch
            {
                AssignmentExpressionSyntax a => a.Left == target,
                PrefixUnaryExpressionSyntax u => u.IsKind(SyntaxKind.PreIncrementExpression) || u.IsKind(SyntaxKind.PreDecrementExpression),
                PostfixUnaryExpressionSyntax => true,
                _ => false
            };
        }
    }

    /// <summary>
    /// The invoked method when the solution declares it in source. An override counts only when the
    /// method it overrides is the solution's too, so a ToString override does not make every
    /// ToString call one.
    /// </summary>
    private static IMethodSymbol? SolutionMethod(InvocationExpressionSyntax inv, SemanticModel model)
    {
        if (model.GetSymbolInfo(inv).Symbol is not IMethodSymbol m) return null;
        m = (m.ReducedFrom ?? m).OriginalDefinition;
        if (m.MethodKind is not (MethodKind.Ordinary or MethodKind.LocalFunction)) return null;
        var root = m;
        while (root.OverriddenMethod is { } o) root = o.OriginalDefinition;
        return root.Locations.Any(l => l.IsInSource) ? m : null;
    }

    /// <summary>Whether a statement always leaves the method: a return or throw, last in its block.</summary>
    private static bool Exits(StatementSyntax s) => s switch
    {
        ReturnStatementSyntax or ThrowStatementSyntax => true,
        BlockSyntax b => b.Statements.LastOrDefault() is { } last && Exits(last),
        _ => false
    };

    /// <summary>
    /// The nesting level the cognitive walk reaches <paramref name="node"/> at, counted the way
    /// <c>CognitiveWalker</c> counts it: if, else, loop, switch section and arm, catch and the
    /// branches of a conditional each add one, and a nested function adds one unless it is the
    /// outermost and sits at the member's own top level.
    /// </summary>
    private static int NestingOf(SyntaxNode node, SyntaxNode body)
    {
        var path = new List<SyntaxNode>();
        for (var n = node; n is not null && n != body; n = n.Parent) path.Add(n);
        path.Reverse();
        int nesting = 0;
        bool inNested = false;
        for (int i = 0; i + 1 < path.Count; i++)
        {
            var (a, c) = (path[i], path[i + 1]);
            switch (a)
            {
                case IfStatementSyntax ifs when c == ifs.Statement:
                case ElseClauseSyntax els when els.Statement is not IfStatementSyntax:
                case ForStatementSyntax or CommonForEachStatementSyntax or WhileStatementSyntax or DoStatementSyntax
                    when c is StatementSyntax:
                case SwitchSectionSyntax or CatchClauseSyntax:
                case SwitchExpressionArmSyntax arm when c == arm.Expression:
                case ConditionalExpressionSyntax cond when c != cond.Condition:
                    nesting++;
                    break;
                case AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax:
                    if (nesting != 0 || inNested) nesting++;
                    inNested = true;
                    break;
            }
        }
        return nesting;
    }

    // ---------------- Flags-driven control flow ----------------

    /// <summary>
    /// Counts HasFlag calls and bitwise-AND tests against a <c>[Flags]</c> enum parameter.
    /// The caller suppresses the resulting penalty for read methods (behavioral gate); a
    /// flags enum steering a read projection is fine, steering a mutation is the smell.
    /// </summary>
    public static int FlagsTestCount(IMethodSymbol method, BaseMethodDeclarationSyntax syntax)
    {
        var flagParams = method.Parameters
            .Where(p => IsFlagsEnum(p.Type))
            .Select(p => p.Name)
            .ToHashSet(StringComparer.Ordinal);
        if (flagParams.Count == 0) return 0;

        SyntaxNode? body = (SyntaxNode?)syntax.Body ?? syntax.ExpressionBody?.Expression;
        if (body is null) return 0;

        int count = 0;
        foreach (var inv in body.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>())
        {
            if (inv.Expression is MemberAccessExpressionSyntax ma
                && ma.Name.Identifier.Text == "HasFlag"
                && ReferencesParam(ma.Expression, flagParams))
                count++;
        }
        foreach (var bin in body.DescendantNodesAndSelf().OfType<BinaryExpressionSyntax>())
        {
            if (bin.Kind() == SyntaxKind.BitwiseAndExpression && ReferencesParam(bin, flagParams))
                count++;
        }
        return count;
    }

    // ---------------- Shared helpers ----------------

    // Read/query "shape" enums steer projection, not mutation, so a write carrying one is not
    // charged as a mode parameter. A selector's arms are judged by what they do whatever its type is
    // called (see AnalyzeDispatcher). Note QueryFlags ends in "Flags" and SearchScope ends in "Scope", both of
    // which are dispatch suffixes, so this exclusion has to run first.
    private static readonly string[] ReadShapeSuffixes = { "ReadShape", "QueryFlags", "Filter", "SearchScope", "Query" };

    private static readonly string[] GenericVerbs = { "Apply", "Handle", "Process", "Execute", "Create", "Save" };

    public static bool IsReadShapeType(ITypeSymbol t)
    {
        var n = t.Name;
        return ReadShapeSuffixes.Any(s => n.EndsWith(s, StringComparison.Ordinal));
    }

    private static bool IsSelectorCandidate(IParameterSymbol p)
    {
        if (p.Type.TypeKind == TypeKind.Enum || p.Type.SpecialType == SpecialType.System_Boolean) return true;
        if (DispatchParamNames.Contains(p.Name)) return true;
        var tn = p.Type.Name;
        return DispatchTypeSuffixes.Any(s => tn.EndsWith(s, StringComparison.Ordinal));
    }

    /// <summary>An enum parameter whose type/name marks it as an action/mode selector (not a read shape).</summary>
    public static bool IsDispatchEnumParam(IParameterSymbol p)
        => p.Type.TypeKind == TypeKind.Enum
           && !IsReadShapeType(p.Type)
           && (DispatchTypeSuffixes.Any(s => p.Type.Name.EndsWith(s, StringComparison.Ordinal))
               || DispatchParamNames.Contains(p.Name));

    public static IReadOnlyList<IParameterSymbol> DispatchEnumParams(IMethodSymbol m)
        => m.Parameters.Where(IsDispatchEnumParam).ToList();

    /// <summary>Method name (less any Async suffix) begins with a generic, content-free verb.</summary>
    public static bool IsGenericVerb(string methodName)
    {
        var n = StripAsync(methodName);
        return GenericVerbs.Any(v => n.StartsWith(v, StringComparison.Ordinal));
    }

    private static readonly string[] StateEngineVocab =
    {
        "Transition", "CanTransition", "StateMachine", "FromState", "ToState",
        "AllowedTransitions", "WorkflowState", "ApplyTransition"
    };

    /// <summary>
    /// Heuristic: does the body read like a real state-machine entry point rather than a thin
    /// action dispatcher? A transition engine validates current state and centralizes transition
    /// rules/side-effects (transition tables, a domain ApplyTransition, From/To state). Those are
    /// legitimate and should NOT draw the generic-dispatcher penalty — the asymmetry is
    /// deliberate: a generic action method is bad <i>unless</i> it proves it is a transition engine.
    /// </summary>
    public static bool LooksLikeStateEngine(BaseMethodDeclarationSyntax syntax)
    {
        SyntaxNode? body = (SyntaxNode?)syntax.Body ?? syntax.ExpressionBody?.Expression;
        if (body is null) return false;
        foreach (var node in body.DescendantNodes())
        {
            string? name = node switch
            {
                IdentifierNameSyntax id => id.Identifier.Text,
                MemberAccessExpressionSyntax ma => ma.Name.Identifier.Text,
                _ => null
            };
            if (name is not null && StateEngineVocab.Any(v => name.Contains(v, StringComparison.Ordinal)))
                return true;
        }
        return false;
    }

    public static bool IsFlagsEnum(ITypeSymbol t)
        => t.TypeKind == TypeKind.Enum
           && t.GetAttributes().Any(a => a.AttributeClass?.Name is "FlagsAttribute" or "Flags");

    private static bool ReferencesParam(SyntaxNode node, HashSet<string> paramNames)
        => node.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>()
            .Any(id => paramNames.Contains(id.Identifier.Text));

    private static string? InvokedName(InvocationExpressionSyntax inv) => inv.Expression switch
    {
        MemberAccessExpressionSyntax ma => ma.Name.Identifier.Text,
        MemberBindingExpressionSyntax mb => mb.Name.Identifier.Text,
        GenericNameSyntax g => g.Identifier.Text,
        IdentifierNameSyntax id => id.Identifier.Text,
        _ => null
    };
}
