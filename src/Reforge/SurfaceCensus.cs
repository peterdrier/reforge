using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Reforge;

/// <summary>
/// What one section exports, counted. Types and methods split two ways: by visibility outside the
/// declaring assembly (exported = public all the way up, see <see cref="SurfaceVisibility"/>) and by
/// behaviour (write = observably mutates persistent state or is command-shaped, see
/// <see cref="ImplementationComplexity.IsMutation"/>). The headline is <see cref="ExportedWriteMethods"/>:
/// a method another assembly can call that changes state is the most expensive kind of surface,
/// and a healthy section has very few.
/// </summary>
/// <remarks>
/// <para>Informational, like <see cref="SectionMetrics"/>: nothing here feeds a score. It is a
/// census of the corpus the rules run over, so it describes the same code the score does.</para>
/// <para><i>Unused externally</i> means no production project other than the declaring one names
/// the symbol anywhere in source. Test projects are not consulted, so an exported type that only
/// tests reach counts as unused: it is exported for the tests' benefit, which is a smell in its
/// own right (<c>InternalsVisibleTo</c> is the cheaper fix). Reflection, DI-by-convention and
/// serialization are invisible to this count, as they are to every static analysis.</para>
/// </remarks>
public sealed record SurfaceCensus(
    int ExportedTypes,
    int InternalTypes,
    int ExportedReadMethods,
    int ExportedWriteMethods,
    int InternalReadMethods,
    int InternalWriteMethods,
    /// <summary>Exported types no other production assembly references.</summary>
    int ExportedTypesUnusedExternally,
    /// <summary>Exported methods no other production assembly references, on any exported type.</summary>
    int ExportedMethodsUnusedExternally,
    /// <summary>Exported write methods no other production assembly references: public mutation nobody outside calls.</summary>
    int ExportedWriteMethodsUnusedExternally)
{
    public static readonly SurfaceCensus Empty = new(0, 0, 0, 0, 0, 0, 0, 0, 0);

    public int ExportedMethods => ExportedReadMethods + ExportedWriteMethods;
    public int InternalMethods => InternalReadMethods + InternalWriteMethods;
}

/// <summary>
/// One cross-assembly reference edge: how many times source in <see cref="From"/> names a symbol
/// declared in <see cref="To"/>. Sections, not assemblies, so a section and its <c>.Contracts</c>
/// satellite fold together and an edge between them never appears.
/// </summary>
public sealed record SectionEdge(string From, string To, int Count);

/// <summary>
/// Counts the exported/internal × read/write surface of every section and, in the same walk, which
/// exported symbols any other production assembly actually names.
/// </summary>
/// <remarks>
/// <para>Methods are ordinary, non-private, non-synthesized methods declared on the classified
/// corpus; accessors belong to their property and are not counted. A method is exported when it
/// is public on an exported type and internal otherwise, which is the same test the surface rules
/// apply. Read/write is decided per method by <see cref="ImplementationComplexity.IsMutation"/>
/// on the method's own body; an interface or abstract method has no body, so it is decided on
/// shape alone (returns data = read, void or bare Task = write).</para>
/// <para>External use is read off one pass over every production document: every simple name
/// whose text matches a corpus type or method name is bound, and when the binding lands on a
/// corpus symbol declared in a different assembly the symbol is marked used. Filtering on the
/// name text first is what keeps this cheap: locals, parameters and framework calls never reach
/// the semantic model. Same-assembly uses are not recorded because they are not surface.</para>
/// </remarks>
public static class SurfaceCensusAnalyzer
{
    public sealed record Result(
        Dictionary<string, SurfaceCensus> BySection,
        SurfaceCensus Solution,
        List<SectionEdge> Edges);

    public static async Task<Result> AnalyzeAsync(
        Solution solution, IReadOnlyList<ClassifiedType> classified, CancellationToken ct)
    {
        // Pass 1 — inventory. Keyed by assembly + display string, as the classifier keys its
        // dedup: a symbol seen from another project's compilation is a different object for the
        // same declaration.
        var types = new Dictionary<string, TypeEntry>(StringComparer.Ordinal);
        var methods = new Dictionary<string, MethodEntry>(StringComparer.Ordinal);
        var typeNames = new HashSet<string>(StringComparer.Ordinal);
        var methodNames = new HashSet<string>(StringComparer.Ordinal);
        var sectionByAssembly = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var c in classified)
        {
            ct.ThrowIfCancellationRequested();
            var assembly = c.Type.ContainingAssembly?.Name ?? "";
            sectionByAssembly[assembly] = c.Group;
            var typeKey = TypeKey(assembly, c.Type);
            types[typeKey] = new TypeEntry(c.Group, c.IsExported);
            typeNames.Add(c.Type.Name);

            foreach (var member in c.Type.GetMembers())
            {
                if (member is not IMethodSymbol m) continue;
                if (m.MethodKind != MethodKind.Ordinary) continue;
                if (m.AssociatedSymbol is not null || m.IsImplicitlyDeclared) continue;
                if (m.DeclaredAccessibility == Accessibility.Private) continue;

                var syntax = SectionMetricsAnalyzer.MethodSyntax(m, ct);
                bool exported = c.IsExported && m.DeclaredAccessibility == Accessibility.Public;
                bool write = ImplementationComplexity.IsMutation(m, syntax);
                methods[MethodKey(assembly, m)] = new MethodEntry(c.Group, exported, write);
                methodNames.Add(m.Name);
            }
        }

        // Pass 2 — who names what, from somewhere else.
        var usedTypes = new HashSet<string>(StringComparer.Ordinal);
        var usedMethods = new HashSet<string>(StringComparer.Ordinal);
        var edges = new Dictionary<(string From, string To), int>();

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
                if (declaringAssembly is null || declaringAssembly == fromAssembly) continue;

                string? toSection = null;
                switch (symbol)
                {
                    case INamedTypeSymbol t:
                    {
                        var key = TypeKey(declaringAssembly, t.OriginalDefinition);
                        if (types.TryGetValue(key, out var entry)) { usedTypes.Add(key); toSection = entry.Section; }
                        break;
                    }
                    case IMethodSymbol m:
                    {
                        var original = (m.ReducedFrom ?? m).OriginalDefinition;
                        var key = MethodKey(declaringAssembly, original);
                        if (methods.TryGetValue(key, out var entry)) { usedMethods.Add(key); toSection = entry.Section; }
                        if (original.ContainingType is { } owner)
                        {
                            var ownerKey = TypeKey(declaringAssembly, owner.OriginalDefinition);
                            if (types.TryGetValue(ownerKey, out var ownerEntry)) { usedTypes.Add(ownerKey); toSection ??= ownerEntry.Section; }
                        }
                        break;
                    }
                    case IPropertySymbol or IFieldSymbol or IEventSymbol:
                    {
                        if (symbol.ContainingType is { } owner)
                        {
                            var ownerKey = TypeKey(declaringAssembly, owner.OriginalDefinition);
                            if (types.TryGetValue(ownerKey, out var ownerEntry)) { usedTypes.Add(ownerKey); toSection = ownerEntry.Section; }
                        }
                        break;
                    }
                }

                if (toSection is not null && !string.Equals(toSection, fromSection, StringComparison.OrdinalIgnoreCase))
                {
                    var edge = (fromSection, toSection);
                    edges[edge] = edges.TryGetValue(edge, out var n) ? n + 1 : 1;
                }
            }
        }

        // Pass 3 — tally.
        var acc = new Dictionary<string, Tally>(StringComparer.OrdinalIgnoreCase);
        var all = new Tally();
        Tally For(string section)
        {
            if (!acc.TryGetValue(section, out var t)) acc[section] = t = new Tally();
            return t;
        }

        foreach (var (key, t) in types)
        {
            bool unused = t.Exported && !usedTypes.Contains(key);
            For(t.Section).Type(t.Exported, unused);
            all.Type(t.Exported, unused);
        }
        foreach (var (key, m) in methods)
        {
            bool unused = m.Exported && !usedMethods.Contains(key);
            For(m.Section).Method(m.Exported, m.Write, unused);
            all.Method(m.Exported, m.Write, unused);
        }

        var edgeList = edges
            .Select(kv => new SectionEdge(kv.Key.From, kv.Key.To, kv.Value))
            .OrderBy(e => e.From, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.To, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new Result(
            acc.ToDictionary(kv => kv.Key, kv => kv.Value.Build(), StringComparer.OrdinalIgnoreCase),
            all.Build(),
            edgeList);
    }

    private sealed record TypeEntry(string Section, bool Exported);
    private sealed record MethodEntry(string Section, bool Exported, bool Write);

    private static string TypeKey(string assembly, INamedTypeSymbol t) => $"{assembly}|{t.ToDisplayString()}";
    private static string MethodKey(string assembly, IMethodSymbol m) => $"{assembly}|{m.ToDisplayString()}";

    private sealed class Tally
    {
        private int _exportedTypes, _internalTypes, _unusedTypes;
        private int _exRead, _exWrite, _inRead, _inWrite, _unusedMethods, _unusedWrite;

        public void Type(bool exported, bool unused)
        {
            if (exported) _exportedTypes++; else _internalTypes++;
            if (unused) _unusedTypes++;
        }

        public void Method(bool exported, bool write, bool unused)
        {
            if (exported) { if (write) _exWrite++; else _exRead++; }
            else { if (write) _inWrite++; else _inRead++; }
            if (unused) { _unusedMethods++; if (write) _unusedWrite++; }
        }

        public SurfaceCensus Build() => new(
            _exportedTypes, _internalTypes,
            _exRead, _exWrite, _inRead, _inWrite,
            _unusedTypes, _unusedMethods, _unusedWrite);
    }
}
