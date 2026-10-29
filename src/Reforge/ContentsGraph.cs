using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Reforge;

/// <summary>
/// Who reaches each census declaration, solution-wide: the input to the depth and sharing factors.
/// </summary>
/// <remarks>
/// <para>An edge is any reference from one member's code to a declaration, invoked or handed on as a
/// method group, and a call through an interface or a virtual member reaches every implementation
/// and override in the corpus. Without that, routing a call through a new interface would read as
/// removing it, and the factors would reward extracting interfaces for nothing.</para>
/// <para>Nothing here reads accessibility. An entry point is a declaration nothing in the corpus
/// reaches (called from a test, the framework, or another solution), so widening a helper never
/// changes its depth or its reach.</para>
/// </remarks>
internal sealed class ContentsGraph
{
    /// <summary>Reach stops counting here: the factor grows with its log, and the walk stays bounded.</summary>
    public const int ReachCap = 1000;

    private readonly Dictionary<string, HashSet<string>> _callers;
    private readonly Dictionary<string, int> _depth;

    private ContentsGraph(Dictionary<string, HashSet<string>> callers, Dictionary<string, int> depth)
    {
        _callers = callers;
        _depth = depth;
    }

    public static ContentsGraph Empty { get; } = new(new(StringComparer.Ordinal), new(StringComparer.Ordinal));

    /// <summary>Members that reference the declaration directly.</summary>
    public int Callers(ISymbol s) => _callers.TryGetValue(Key(s), out var c) ? c.Count : 0;

    /// <summary>
    /// Shortest distance from a declaration nothing reaches. 0 for an entry point; 1 for one that sits
    /// only on cycles no entry point reaches, since something does call it.
    /// </summary>
    public int Depth(ISymbol s) => _depth.TryGetValue(Key(s), out var d) ? d : 0;

    /// <summary>
    /// Distinct members that reach the declaration through any chain of references, up to
    /// <see cref="ReachCap"/>. Transitive so that a forwarding wrapper in front of shared code adds
    /// to its reach instead of hiding it.
    /// </summary>
    public int Reach(ISymbol s)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        queue.Enqueue(Key(s));
        while (queue.Count > 0 && seen.Count < ReachCap)
        {
            if (!_callers.TryGetValue(queue.Dequeue(), out var cs)) continue;
            foreach (var c in cs)
                if (seen.Add(c)) queue.Enqueue(c);
        }
        return Math.Min(seen.Count, ReachCap);
    }

    public static async Task<ContentsGraph> BuildAsync(Solution solution,
        IEnumerable<ContentsUnit> units, HashSet<string> analyzedAssemblies, CancellationToken ct)
    {
        var nodes = new HashSet<string>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.Ordinal);
        var dispatch = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var types = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        foreach (var u in units)
        {
            nodes.Add(Key(u.Symbol));
            names.Add(u.Symbol is IMethodSymbol { MethodKind: MethodKind.Constructor } ? u.Symbol.ContainingType.Name : u.Symbol.Name);
            types.Add(u.Symbol.ContainingType);
            for (var o = Overridden(u.Symbol); o is not null; o = Overridden(o))
                AddDispatch(dispatch, names, o, u.Symbol);
        }
        foreach (var t in types)
        {
            if (t.TypeKind == TypeKind.Interface) continue;
            foreach (var member in t.AllInterfaces.SelectMany(i => i.GetMembers()))
                if (t.FindImplementationForInterfaceMember(member) is { } impl && nodes.Contains(Key(impl)))
                    AddDispatch(dispatch, names, member, impl);
        }

        var callers = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var callees = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var project in solution.Projects)
        {
            ct.ThrowIfCancellationRequested();
            if (project.AssemblyName is null || !analyzedAssemblies.Contains(project.AssemblyName)) continue;
            var compilation = await project.GetCompilationAsync(ct);
            if (compilation is null) continue;
            foreach (var tree in compilation.SyntaxTrees)
            {
                if (GeneratedCode.IsGeneratedFile(tree.FilePath)) continue;
                var model = compilation.GetSemanticModel(tree);
                foreach (var node in (await tree.GetRootAsync(ct)).DescendantNodes())
                {
                    var referenced = Referenced(node, model, names, ct);
                    if (referenced is null) continue;
                    var from = Enclosing(node, model, ct);
                    if (from is null) continue;
                    var to = Key(referenced);
                    if (nodes.Contains(to)) Link(callers, callees, from, to);
                    if (dispatch.TryGetValue(to, out var impls))
                        foreach (var impl in impls) Link(callers, callees, from, impl);
                }
            }
        }

        return new ContentsGraph(callers, Depths(nodes, callers, callees));
    }

    /// <summary>
    /// Breadth-first from every member nothing reaches. A caller that is not itself a census
    /// declaration (a field initializer, say) has no callers recorded, so it starts at 0 too.
    /// </summary>
    private static Dictionary<string, int> Depths(HashSet<string> nodes,
        Dictionary<string, HashSet<string>> callers, Dictionary<string, HashSet<string>> callees)
    {
        var depth = new Dictionary<string, int>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        foreach (var k in nodes.Concat(callees.Keys))
            if (!callers.ContainsKey(k) && depth.TryAdd(k, 0)) queue.Enqueue(k);
        while (queue.Count > 0)
        {
            var k = queue.Dequeue();
            if (!callees.TryGetValue(k, out var next)) continue;
            foreach (var n in next)
                if (depth.TryAdd(n, depth[k] + 1)) queue.Enqueue(n);
        }
        foreach (var k in nodes) depth.TryAdd(k, 1);
        return depth;
    }

    private static void Link(Dictionary<string, HashSet<string>> callers,
        Dictionary<string, HashSet<string>> callees, string from, string to)
    {
        if (from == to) return;
        if (!callers.TryGetValue(to, out var cs)) callers[to] = cs = new HashSet<string>(StringComparer.Ordinal);
        cs.Add(from);
        if (!callees.TryGetValue(from, out var es)) callees[from] = es = new HashSet<string>(StringComparer.Ordinal);
        es.Add(to);
    }

    private static void AddDispatch(Dictionary<string, HashSet<string>> dispatch, HashSet<string> names,
        ISymbol declared, ISymbol implementation)
    {
        var key = Key(declared);
        if (!dispatch.TryGetValue(key, out var set)) dispatch[key] = set = new HashSet<string>(StringComparer.Ordinal);
        set.Add(Key(implementation));
        names.Add(declared.Name);
    }

    private static ISymbol? Overridden(ISymbol s) => s switch
    {
        IMethodSymbol m => m.OverriddenMethod,
        IPropertySymbol p => p.OverriddenProperty,
        IEventSymbol e => e.OverriddenEvent,
        _ => null
    };

    /// <summary>
    /// The member a node refers to, when the node can name a census declaration: a simple name, an
    /// object creation (its constructor), or a <c>base(...)</c>/<c>this(...)</c> initializer. Names
    /// are pre-filtered, so most of the corpus's identifiers never reach the semantic model.
    /// </summary>
    private static ISymbol? Referenced(SyntaxNode node, SemanticModel model, HashSet<string> names, CancellationToken ct)
    {
        switch (node)
        {
            case SimpleNameSyntax name when names.Contains(name.Identifier.ValueText):
                break;
            case ObjectCreationExpressionSyntax { Type: var type } when names.Contains(RightmostName(type)):
                break;
            case ImplicitObjectCreationExpressionSyntax or ConstructorInitializerSyntax:
                break;
            default:
                return null;
        }
        return model.GetSymbolInfo(node, ct).Symbol switch
        {
            IMethodSymbol m => m.ReducedFrom ?? m,
            ISymbol s and (IPropertySymbol or IFieldSymbol or IEventSymbol) => s,
            _ => null
        };
    }

    private static string RightmostName(TypeSyntax type) => type switch
    {
        QualifiedNameSyntax q => q.Right.Identifier.ValueText,
        SimpleNameSyntax s => s.Identifier.ValueText,
        AliasQualifiedNameSyntax a => a.Name.Identifier.ValueText,
        _ => ""
    };

    /// <summary>
    /// The member whose code holds the node. Lambdas, local functions and accessors are stepped over:
    /// their references belong to the member they sit in, as their complexity does.
    /// </summary>
    private static string? Enclosing(SyntaxNode node, SemanticModel model, CancellationToken ct)
    {
        for (var n = node.Parent; n is not null; n = n.Parent)
        {
            switch (n)
            {
                case BaseMethodDeclarationSyntax or BasePropertyDeclarationSyntax:
                case VariableDeclaratorSyntax { Parent.Parent: BaseFieldDeclarationSyntax }:
                    return model.GetDeclaredSymbol(n, ct) is { } s ? Key(s) : null;
                case GlobalStatementSyntax:
                    // Top-level statements are the program's entry point, a member with no name.
                    return $"{model.Compilation.AssemblyName}|<top-level statements>";
                case BaseTypeDeclarationSyntax:
                    return null;
            }
        }
        return null;
    }

    private static string Key(ISymbol s) => CallPathComplexity.Key(s.OriginalDefinition);
}
