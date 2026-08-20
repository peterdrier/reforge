using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Reforge;

// Pass 5 — write-capable interface used read-only. The most expensive pass: it needs the
// semantic model to see how each injected dependency is actually called.
public sealed partial class SurfaceScoreEngine
{
    /// <summary>
    /// Symbol-based (no name-prefix guessing). For each full-service interface paired with a
    /// read-service interface (inheritance, or a sibling named "{Full}Read" in the same
    /// namespace), check every consumer that injects the full interface. If every observed
    /// invocation on that injected dependency targets a method that ALSO exists on the read
    /// interface (same name + arity), the consumer doesn't need write capability and the rule
    /// fires +12 against the consumer's section. A single full-only call cancels the rule.
    /// </summary>
    private async Task ScoreWriteCapableUsedReadOnlyAsync(
        List<ClassifiedType> classified,
        Dictionary<string, ClassifiedType> typesByDisplay,
        Solution solution,
        ScoreReport report,
        CancellationToken ct)
    {
        var weight = _config.Weight("writeCapableInterfaceUsedReadOnly");
        if (weight == 0) return;

        // Build full -> read pairs once. A pair is established when:
        //   - the full interface directly inherits a classified read-service interface, OR
        //   - a classified read-service interface with name "{full.Name}Read" or "Read{stripped}"
        //     lives in the same namespace.
        var pairs = BuildFullToReadPairs(classified, typesByDisplay);
        if (pairs.Count == 0) return;

        // For each full interface, collect the set of method (name, arity) tuples on the read
        // interface. A call on the full interface counts as "read-covered" iff this set
        // contains its target method's (name, arity).
        var readMethodIndex = pairs.ToDictionary(
            kv => kv.Key,
            kv => new HashSet<(string Name, int Arity)>(
                kv.Value.Type.GetMembers().OfType<IMethodSymbol>()
                    .Where(m => m.MethodKind == MethodKind.Ordinary)
                    .Select(m => (m.Name, m.Parameters.Length))),
            StringComparer.Ordinal);

        // Index full interfaces by display string for fast lookup during the syntax walk.
        var fullByDisplay = pairs.Keys.ToHashSet(StringComparer.Ordinal);

        foreach (var c in classified)
        {
            if (c.Type.TypeKind != TypeKind.Class) continue;

            // Find every constructor param whose type is one of our paired full interfaces.
            // For each such injection, we'll walk the class body looking at how this dep is used.
            var injectedFulls = new List<(string FullDisplay, IParameterSymbol Param)>();
            foreach (var ctor in c.Type.Constructors)
            {
                if (ctor.IsImplicitlyDeclared) continue;
                foreach (var p in ctor.Parameters)
                {
                    var d = SolutionClassifier.TypeKey(p.Type);
                    if (fullByDisplay.Contains(d))
                        injectedFulls.Add((d, p));
                }
            }
            if (injectedFulls.Count == 0) continue;

            // Call counts per injected dependency, accumulated across EVERY declaring tree before
            // anything is charged. A partial class is one consumer however many files it is split
            // across: counting per declaration charged it once per file, and let a write call in
            // one half fail to cancel the rule for the other — so the score depended on file
            // layout, which is exactly the kind of edit that must not move it.
            var calls = new Dictionary<string, (int Read, int FullOnly)>(StringComparer.Ordinal);

            foreach (var declRef in c.Type.DeclaringSyntaxReferences)
            {
                var tree = declRef.SyntaxTree;
                var project = solution.Projects.FirstOrDefault(p => p.Documents.Any(d => d.FilePath == tree.FilePath));
                if (project is null) continue;
                var compilation = await project.GetCompilationAsync(ct);
                if (compilation is null) continue;
                var model = compilation.GetSemanticModel(tree);
                var classNode = await declRef.GetSyntaxAsync(ct);

                foreach (var (fullDisplay, _) in injectedFulls)
                {
                    var readSet = readMethodIndex[fullDisplay];
                    var (readCalls, fullOnlyCalls) = calls.GetValueOrDefault(fullDisplay);

                    foreach (var invocation in classNode.DescendantNodes().OfType<InvocationExpressionSyntax>())
                    {
                        if (invocation.Expression is not MemberAccessExpressionSyntax ma) continue;

                        // Resolve the receiver's type. The injected interface is held by a
                        // field assigned in the ctor; the field's declared type matches the
                        // ctor param's type, so checking against the full display string covers
                        // both `_dep.Foo()` and `dep.Foo()` (constructor-only stash).
                        var receiverType = model.GetTypeInfo(ma.Expression).Type;
                        if (receiverType is null) continue;
                        if (!string.Equals(SolutionClassifier.TypeKey(receiverType), fullDisplay, StringComparison.Ordinal))
                            continue;

                        var methodName = ma.Name.Identifier.Text;
                        // C# allows omitting optional args, so we can't insist on exact arity.
                        // Accept a read-cover match if ANY read-interface method has the same name.
                        // (Overloads are rare in practice; this loosens the check enough to
                        // handle default arguments while staying symbol-grounded.)
                        if (readSet.Any(t => t.Name == methodName))
                            readCalls++;
                        else
                            fullOnlyCalls++;
                    }

                    calls[fullDisplay] = (readCalls, fullOnlyCalls);
                }
            }

            foreach (var (fullDisplay, param) in injectedFulls)
            {
                var (readCalls, fullOnlyCalls) = calls.GetValueOrDefault(fullDisplay);
                if (fullOnlyCalls != 0 || readCalls == 0) continue;

                var readName = pairs[fullDisplay].Type.Name;
                var fullName = param.Type.Name;
                var loc = param.Locations.FirstOrDefault(l => l.IsInSource) ?? c.PrimaryLocation;
                var (file, line) = LocateMember(loc, c);
                AddEntry(report, c.Group, "writeCapableInterfaceUsedReadOnly", weight, c.Type, file, line,
                    $"{c.Type.Name} <- {fullName} (use {readName} instead; {readCalls} read calls, 0 write calls)");
            }
        }
    }

    internal static Dictionary<string, ClassifiedType> BuildFullToReadPairs(
        List<ClassifiedType> classified,
        Dictionary<string, ClassifiedType> typesByDisplay)
    {
        var pairs = new Dictionary<string, ClassifiedType>(StringComparer.Ordinal);

        var fullInterfaces = classified.Where(c =>
            c.Type.TypeKind == TypeKind.Interface && c.Tags.Contains("fullServiceInterface")).ToList();
        var readInterfaces = classified.Where(c =>
            c.Type.TypeKind == TypeKind.Interface && c.Tags.Contains("readServiceInterface")).ToList();
        if (fullInterfaces.Count == 0 || readInterfaces.Count == 0) return pairs;

        // Keyed by TypeKey for the same reason as typesByDisplay: a display name is unique per
        // assembly, not solution-wide.
        var readByDisplay = readInterfaces.ToDictionary(
            r => SolutionClassifier.TypeKey(r.Type), r => r, StringComparer.Ordinal);
        var readByNameInNamespace = readInterfaces.ToLookup(
            r => $"{r.Type.ContainingNamespace?.ToDisplayString()}|{r.Type.Name}",
            StringComparer.Ordinal);

        foreach (var full in fullInterfaces)
        {
            // Strategy 1: direct inheritance. The full interface lists the read interface as a base.
            var inheritedRead = full.Type.Interfaces.FirstOrDefault(i =>
                readByDisplay.ContainsKey(SolutionClassifier.TypeKey(i)));
            if (inheritedRead is not null)
            {
                pairs[SolutionClassifier.TypeKey(full.Type)] = readByDisplay[SolutionClassifier.TypeKey(inheritedRead)];
                continue;
            }

            // Strategy 2: same-namespace sibling named "{full.Name}Read" — e.g. IUserService
            // pairs with IUserServiceRead in the same namespace. This catches the common
            // Humans-style layout where read and full are siblings rather than parent-child.
            var ns = full.Type.ContainingNamespace?.ToDisplayString() ?? "";
            var siblingKey = $"{ns}|{full.Type.Name}Read";
            var sibling = readByNameInNamespace[siblingKey].FirstOrDefault();
            if (sibling is not null)
            {
                pairs[SolutionClassifier.TypeKey(full.Type)] = sibling;
            }
        }

        return pairs;
    }
}
