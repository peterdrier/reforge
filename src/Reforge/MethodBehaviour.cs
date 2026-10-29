using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Reforge;

/// <summary>
/// What every method in the classified corpus does (read or write) and which exported symbols any
/// other production assembly names, resolved once per run from one pass over every production
/// document. Both the surface census and the <c>exportedReadMethod</c> /
/// <c>exportedWriteMethod</c> / <c>exportedUnusedExternally</c> rules read this, so the number a
/// census reports and the number a rule charges are the same number.
/// </summary>
/// <remarks>
/// <para><b>Write</b> is decided per method, behaviourally, and the verdict follows calls. A method
/// is a write when its own body commits persistent state, when it is command-shaped (returns no
/// data; see <see cref="ImplementationComplexity.IsMutation"/>), or when any corpus method it calls
/// (transitively) commits persistent state. The last clause is what keeps the verdict
/// rename-proof and <i>return-type-proof</i>: making <c>Task UpdateAsync()</c> return
/// <c>Task&lt;bool&gt;</c> does not turn it into a read while it still reaches <c>SaveChanges</c>
/// through the repository it calls. Only definitive commits propagate; the command-shape heuristic
/// applies to a method's own signature and never travels, so a read that calls a <c>void</c> helper
/// stays a read.</para>
/// <para>An interface method has no body, so it is a write when any implementation in the corpus
/// is a write. With no implementation in the corpus the verdict falls back to the declaration's
/// shape, which is the most that can be known.</para>
/// <para><b>Used externally</b> means some production document in a different assembly binds a
/// simple name to the symbol. Test projects are not walked; reflection, convention DI and
/// serialization are invisible, as they are to every static analysis.</para>
/// </remarks>
public sealed class MethodBehaviourMap
{
    private readonly Dictionary<string, bool> _writes;
    private readonly HashSet<string> _usedMethods;
    private readonly HashSet<string> _usedTypes;

    internal MethodBehaviourMap(Dictionary<string, bool> writes, HashSet<string> usedMethods,
        HashSet<string> usedTypes, List<SectionEdge> edges)
    {
        _writes = writes;
        _usedMethods = usedMethods;
        _usedTypes = usedTypes;
        Edges = edges;
    }

    public static readonly MethodBehaviourMap Empty =
        new(new(StringComparer.Ordinal), new(StringComparer.Ordinal), new(StringComparer.Ordinal), new());

    /// <summary>Cross-section reference edges with counts, from the same walk.</summary>
    public List<SectionEdge> Edges { get; }

    /// <summary>True when the method is a write; a method outside the corpus is decided on its shape alone.</summary>
    public bool IsWrite(IMethodSymbol method) =>
        _writes.TryGetValue(Key(method), out var w) ? w : ImplementationComplexity.IsMutation(method, null);

    public bool IsUsedExternally(IMethodSymbol method) => _usedMethods.Contains(Key(method));
    public bool IsUsedExternally(INamedTypeSymbol type) => _usedTypes.Contains(Key(type));

    /// <summary>Declaring assembly + display string: the one key that survives a symbol being seen from another compilation.</summary>
    internal static string Key(ISymbol symbol)
    {
        var s = symbol is IMethodSymbol m ? (m.ReducedFrom ?? m).OriginalDefinition : symbol.OriginalDefinition;
        return $"{s.ContainingAssembly?.Name}|{s.ToDisplayString()}";
    }
}

public static class MethodBehaviourAnalyzer
{
    public static async Task<MethodBehaviourMap> AnalyzeAsync(
        Solution solution, IReadOnlyList<ClassifiedType> classified, CancellationToken ct)
    {
        // Pass 1 — inventory. Every corpus type and every non-private ordinary method on it, with
        // the method's own verdict (shape plus in-body commits) as the seed for propagation.
        var typeSection = new Dictionary<string, string>(StringComparer.Ordinal);
        var methodSection = new Dictionary<string, string>(StringComparer.Ordinal);
        var ownWrite = new Dictionary<string, bool>(StringComparer.Ordinal);
        var commits = new HashSet<string>(StringComparer.Ordinal);
        var bodies = new Dictionary<string, BaseMethodDeclarationSyntax>(StringComparer.Ordinal);
        var interfaceMethods = new List<(string Key, IMethodSymbol Method, INamedTypeSymbol Interface)>();
        var typeNames = new HashSet<string>(StringComparer.Ordinal);
        var methodNames = new HashSet<string>(StringComparer.Ordinal);
        var sectionByAssembly = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var c in classified)
        {
            ct.ThrowIfCancellationRequested();
            var assembly = c.Type.ContainingAssembly?.Name ?? "";
            sectionByAssembly[assembly] = c.Group;
            typeSection[MethodBehaviourMap.Key(c.Type)] = c.Group;
            typeNames.Add(c.Type.Name);

            foreach (var m in CorpusMethods(c.Type))
            {
                var key = MethodBehaviourMap.Key(m);
                methodSection[key] = c.Group;
                methodNames.Add(m.Name);

                if (c.Type.TypeKind == TypeKind.Interface || m.IsAbstract)
                {
                    interfaceMethods.Add((key, m, c.Type));
                    ownWrite[key] = ImplementationComplexity.IsMutation(m, null);
                    continue;
                }

                var syntax = SectionMetricsAnalyzer.MethodSyntax(m, ct);
                if (syntax is not null) bodies[key] = syntax;
                var body = (SyntaxNode?)syntax?.Body ?? syntax?.ExpressionBody?.Expression;
                if (ImplementationComplexity.CommitsPersistentWrite(body)) commits.Add(key);
                ownWrite[key] = ImplementationComplexity.IsMutation(m, syntax);
            }
        }

        // Pass 2 — one walk: external use of corpus symbols, cross-section edges, and the calls
        // between corpus methods that the commit verdict propagates along.
        var usedTypes = new HashSet<string>(StringComparer.Ordinal);
        var usedMethods = new HashSet<string>(StringComparer.Ordinal);
        var edges = new Dictionary<(string From, string To), int>();
        var calls = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        await foreach (var doc in SolutionWalker.ProductionDocumentsAsync(solution, ct))
        {
            var fromAssembly = doc.Project.AssemblyName;
            if (!sectionByAssembly.TryGetValue(fromAssembly, out var fromSection)) continue;

            foreach (var name in doc.Root.DescendantNodes().OfType<SimpleNameSyntax>())
            {
                var text = name.Identifier.ValueText;
                bool maybeType = typeNames.Contains(text);
                bool maybeMethod = methodNames.Contains(text);
                if (!maybeType && !maybeMethod) continue;

                var info = doc.Model.GetSymbolInfo(name, ct);
                var symbol = info.Symbol ?? (info.CandidateSymbols.Length > 0 ? info.CandidateSymbols[0] : null);
                if (symbol is null) continue;
                var declaringAssembly = symbol.ContainingAssembly?.Name;
                if (declaringAssembly is null) continue;

                string? toSection = null;
                string? calleeKey = null;
                switch (symbol)
                {
                    case INamedTypeSymbol t:
                    {
                        var key = MethodBehaviourMap.Key(t);
                        if (typeSection.TryGetValue(key, out var s)) { toSection = s; if (declaringAssembly != fromAssembly) usedTypes.Add(key); }
                        break;
                    }
                    case IMethodSymbol m:
                    {
                        var key = MethodBehaviourMap.Key(m);
                        if (methodSection.TryGetValue(key, out var s)) { toSection = s; calleeKey = key; if (declaringAssembly != fromAssembly) usedMethods.Add(key); }
                        if ((m.ReducedFrom ?? m).OriginalDefinition.ContainingType is { } owner)
                        {
                            var ownerKey = MethodBehaviourMap.Key(owner);
                            if (typeSection.TryGetValue(ownerKey, out var os)) { toSection ??= os; if (declaringAssembly != fromAssembly) usedTypes.Add(ownerKey); }
                        }
                        break;
                    }
                    case IPropertySymbol or IFieldSymbol or IEventSymbol:
                    {
                        if (symbol.ContainingType is { } owner)
                        {
                            var ownerKey = MethodBehaviourMap.Key(owner);
                            if (typeSection.TryGetValue(ownerKey, out var os)) { toSection = os; if (declaringAssembly != fromAssembly) usedTypes.Add(ownerKey); }
                        }
                        break;
                    }
                }

                if (toSection is null) continue;

                if (declaringAssembly != fromAssembly
                    && !string.Equals(toSection, fromSection, StringComparison.OrdinalIgnoreCase))
                {
                    var edge = (fromSection, toSection);
                    edges[edge] = edges.TryGetValue(edge, out var n) ? n + 1 : 1;
                }

                // A call from inside a corpus method body: an edge the commit verdict travels along.
                if (calleeKey is not null && name.Parent is not null)
                {
                    var caller = EnclosingMethod(name, doc.Model, ct);
                    if (caller is not null)
                    {
                        var callerKey = MethodBehaviourMap.Key(caller);
                        if (ownWrite.ContainsKey(callerKey) && callerKey != calleeKey)
                        {
                            if (!calls.TryGetValue(callerKey, out var set)) calls[callerKey] = set = new HashSet<string>(StringComparer.Ordinal);
                            set.Add(calleeKey);
                        }
                    }
                }
            }
        }

        // Pass 3 — propagate commits along calls to a fixpoint. Interface methods take the verdict
        // of their implementations, which are resolved after propagation so a write reached through
        // a helper counts on the interface too.
        var reachesCommit = new HashSet<string>(commits, StringComparer.Ordinal);
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (var (caller, callees) in calls)
            {
                if (reachesCommit.Contains(caller)) continue;
                if (callees.Any(reachesCommit.Contains)) { reachesCommit.Add(caller); changed = true; }
            }
        }

        var writes = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var (key, own) in ownWrite)
            writes[key] = own || reachesCommit.Contains(key);

        var classes = classified.Where(c => c.Type.TypeKind is TypeKind.Class or TypeKind.Struct).ToList();
        foreach (var (key, method, owner) in interfaceMethods)
        {
            bool anyImpl = false, anyWrite = false;
            foreach (var cls in classes)
            {
                if (owner.TypeKind == TypeKind.Interface)
                {
                    if (!cls.Type.AllInterfaces.Any(i => SymbolEqualityComparer.Default.Equals(i.OriginalDefinition, owner.OriginalDefinition))) continue;
                    var impl = cls.Type.FindImplementationForInterfaceMember(method) as IMethodSymbol;
                    if (impl is null) continue;
                    anyImpl = true;
                    if (writes.TryGetValue(MethodBehaviourMap.Key(impl), out var w) && w) { anyWrite = true; break; }
                }
                else
                {
                    // Abstract method on a class: an override in a derived corpus class decides it.
                    if (!DerivesFrom(cls.Type, owner)) continue;
                    var over = cls.Type.GetMembers(method.Name).OfType<IMethodSymbol>()
                        .FirstOrDefault(o => o.OverriddenMethod is { } b && SymbolEqualityComparer.Default.Equals(b.OriginalDefinition, method.OriginalDefinition));
                    if (over is null) continue;
                    anyImpl = true;
                    if (writes.TryGetValue(MethodBehaviourMap.Key(over), out var w) && w) { anyWrite = true; break; }
                }
            }
            if (anyImpl) writes[key] = anyWrite;
            // else: keep the shape verdict seeded in pass 1.
        }

        var edgeList = edges
            .Select(kv => new SectionEdge(kv.Key.From, kv.Key.To, kv.Value))
            .OrderBy(e => e.From, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.To, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new MethodBehaviourMap(writes, usedMethods, usedTypes, edgeList);
    }

    /// <summary>Ordinary, non-private, non-synthesized methods: the population both the census and the rules count.</summary>
    public static IEnumerable<IMethodSymbol> CorpusMethods(INamedTypeSymbol type)
    {
        foreach (var member in type.GetMembers())
        {
            if (member is not IMethodSymbol m) continue;
            if (m.MethodKind != MethodKind.Ordinary) continue;
            if (m.AssociatedSymbol is not null || m.IsImplicitlyDeclared) continue;
            if (m.DeclaredAccessibility == Accessibility.Private) continue;
            yield return m;
        }
    }

    private static IMethodSymbol? EnclosingMethod(SyntaxNode node, SemanticModel model, CancellationToken ct)
    {
        for (var n = node.Parent; n is not null; n = n.Parent)
        {
            if (n is BaseMethodDeclarationSyntax decl)
                return model.GetDeclaredSymbol(decl, ct) as IMethodSymbol;
            if (n is TypeDeclarationSyntax) return null;
        }
        return null;
    }

    private static bool DerivesFrom(INamedTypeSymbol type, INamedTypeSymbol baseType)
    {
        for (var t = type.BaseType; t is not null; t = t.BaseType)
            if (SymbolEqualityComparer.Default.Equals(t.OriginalDefinition, baseType.OriginalDefinition)) return true;
        return false;
    }
}
