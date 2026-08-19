using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Reforge;

/// <summary>
/// Which scoring axis a rule belongs to. The surface axis (durable public surface,
/// dependency use, return shape) and the internal-complexity axis (implementation
/// cost of what hides behind that surface) are kept as <b>separate scalars</b> on
/// purpose. They are never summed into a single number that a refactoring loop is
/// allowed to optimize — that would let the loop trade surface for complexity and
/// still "improve" the net. The combined total is informational only; the real
/// signal is the per-axis Pareto comparison in <see cref="SurfaceScoreBaseline"/>.
/// </summary>
public static class SurfaceScoreRuleGroups
{
    public static readonly IReadOnlySet<string> InternalComplexity = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "longMethod",
        "largeClass",
        "cognitiveComplexity",
        "actionDispatcher",
        "mutationModeParameter",
        "flagsControlFlow"
    };

    public static bool IsInternalComplexity(string rule) => InternalComplexity.Contains(rule);
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

    /// <summary>longMethod base points: +1 per 10 nonblank LOC over 40, +10 over 100, +25 over 180.</summary>
    public static int LongMethodPoints(int loc)
    {
        if (loc <= 40) return 0;
        int p = (loc - 40) / 10;
        if (loc > 100) p += 10;
        if (loc > 180) p += 25;
        return p;
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
    {
        var w = new CognitiveWalker();
        if (method.Body is { } block)
            w.VisitChildren(block, 0);
        else if (method.ExpressionBody is { } arrow)
            w.Visit(arrow.Expression, 0);
        return new CognitiveScore(w.Score, w.TopNestedScore, w.TopNestedLine);
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

    // ---------------- Structural dispatcher (arm cohesion) ----------------

    public readonly record struct DispatcherAnalysis(bool Fires, int ArmCount);

    private static readonly HashSet<string> DispatchParamNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "action", "mode", "operation", "command", "scope", "flags", "options", "kind", "type"
    };

    private static readonly string[] DispatchTypeSuffixes = { "Action", "Mode", "Operation", "Scope", "Flags", "Kind" };

    /// <summary>
    /// Detects the "generic action dispatcher" shape structurally: a method whose body
    /// is essentially one switch/if-chain over a candidate parameter, where the arms
    /// route to <i>different</i> members with disjoint bodies. That low arm-cohesion is
    /// the fingerprint of explicit commands collapsed behind one method. A read-shape
    /// consolidation looks different — its arms share a base query and differ only by
    /// Include/Where/projection — so one member is called by ≥ half the arms and the
    /// rule does not fire. The method name is never consulted (no Apply/Handle/Create
    /// heuristic), which removes the Create-verb contradiction.
    /// </summary>
    public static DispatcherAnalysis AnalyzeDispatcher(IMethodSymbol method, BaseMethodDeclarationSyntax syntax)
    {
        SyntaxNode? body = (SyntaxNode?)syntax.Body ?? syntax.ExpressionBody?.Expression;
        if (body is null) return default;

        var paramNames = method.Parameters
            .Where(IsDispatchParam)
            .Select(p => p.Name)
            .ToHashSet(StringComparer.Ordinal);
        if (paramNames.Count == 0) return default;

        DispatcherAnalysis best = default;

        foreach (var sw in body.DescendantNodesAndSelf().OfType<SwitchStatementSyntax>())
        {
            if (!ReferencesParam(sw.Expression, paramNames)) continue;
            var arms = sw.Sections.Select(s => InvokedMembers(s)).ToList();
            best = Better(best, Evaluate(arms));
        }

        foreach (var swe in body.DescendantNodesAndSelf().OfType<SwitchExpressionSyntax>())
        {
            if (!ReferencesParam(swe.GoverningExpression, paramNames)) continue;
            var arms = swe.Arms.Select(a => InvokedMembers(a.Expression)).ToList();
            best = Better(best, Evaluate(arms));
        }

        foreach (var ifs in body.DescendantNodesAndSelf().OfType<IfStatementSyntax>())
        {
            if (ifs.Parent is ElseClauseSyntax) continue; // only chain heads
            if (!ReferencesParam(ifs.Condition, paramNames)) continue;

            var arms = new List<HashSet<string>>();
            IfStatementSyntax? cur = ifs;
            while (cur is not null)
            {
                if (!ReferencesParam(cur.Condition, paramNames)) break;
                arms.Add(InvokedMembers(cur.Statement));
                if (cur.Else?.Statement is IfStatementSyntax next)
                {
                    cur = next;
                }
                else
                {
                    if (cur.Else is { } tail) arms.Add(InvokedMembers(tail.Statement));
                    cur = null;
                }
            }
            best = Better(best, Evaluate(arms));
        }

        return best;
    }

    private static DispatcherAnalysis Better(DispatcherAnalysis a, DispatcherAnalysis b)
        => b.ArmCount > a.ArmCount ? b : a;

    private static DispatcherAnalysis Evaluate(List<HashSet<string>> arms)
    {
        if (arms.Count < 2) return default;

        var union = new HashSet<string>(StringComparer.Ordinal);
        foreach (var a in arms) union.UnionWith(a);
        if (union.Count < 2) return default; // arms don't delegate to ≥2 distinct members

        // Shared-body check: if one member is called by ≥ half the arms, the arms share a
        // base body (the read-shape pattern) and this is cohesive, not a dispatcher.
        int maxShared = 0;
        foreach (var m in union)
        {
            int c = arms.Count(a => a.Contains(m));
            if (c > maxShared) maxShared = c;
        }
        if ((double)maxShared / arms.Count >= 0.5) return default;

        return new DispatcherAnalysis(true, arms.Count);
    }

    private static HashSet<string> InvokedMembers(SyntaxNode arm)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var inv in arm.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>())
        {
            var name = InvokedName(inv);
            if (name is not null) set.Add(name);
        }
        return set;
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

    // Read/query "shape" enums steer projection, not mutation. They must NOT be treated as
    // dispatch parameters — otherwise Get*(RotaReadShape) / Search*(ShiftEventQueryFlags) would
    // be falsely penalized. Note QueryFlags ends in "Flags" and SearchScope ends in "Scope",
    // both of which are dispatch suffixes, so this exclusion has to run first.
    private static readonly string[] ReadShapeSuffixes = { "ReadShape", "QueryFlags", "Filter", "SearchScope", "Query" };

    private static readonly string[] GenericVerbs = { "Apply", "Handle", "Process", "Execute", "Create", "Save" };

    public static bool IsReadShapeType(ITypeSymbol t)
    {
        var n = t.Name;
        return ReadShapeSuffixes.Any(s => n.EndsWith(s, StringComparison.Ordinal));
    }

    public static bool IsDispatchParam(IParameterSymbol p)
    {
        if (IsReadShapeType(p.Type)) return false;
        if (p.Type.TypeKind == TypeKind.Enum) return true;
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
