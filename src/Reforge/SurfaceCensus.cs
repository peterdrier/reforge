using Microsoft.CodeAnalysis;

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
/// the symbol anywhere in source; see <see cref="MethodBehaviourMap"/> for what that walk can and
/// cannot see.</para>
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
/// Tallies the exported/internal × read/write surface of every section from a
/// <see cref="MethodBehaviourMap"/>: the same verdicts the <c>exported*Method</c> rules charge.
/// </summary>
/// <remarks>
/// Methods are <see cref="MethodBehaviourAnalyzer.CorpusMethods"/> on every classified type, DTOs
/// and controllers included: this is a census of what is exported, wider than the set any one rule
/// prices. A method is exported when it is public on an exported type and internal otherwise, which
/// is the test the surface rules apply.
/// </remarks>
public static class SurfaceCensusAnalyzer
{
    public sealed record Result(
        Dictionary<string, SurfaceCensus> BySection,
        SurfaceCensus Solution,
        List<SectionEdge> Edges);

    public static Result Analyze(IReadOnlyList<ClassifiedType> classified, MethodBehaviourMap behaviour)
    {
        var acc = new Dictionary<string, Tally>(StringComparer.OrdinalIgnoreCase);
        var all = new Tally();
        Tally For(string section)
        {
            if (!acc.TryGetValue(section, out var t)) acc[section] = t = new Tally();
            return t;
        }

        foreach (var c in classified)
        {
            bool typeUnused = c.IsExported && !behaviour.IsUsedExternally(c.Type);
            For(c.Group).Type(c.IsExported, typeUnused);
            all.Type(c.IsExported, typeUnused);

            foreach (var m in MethodBehaviourAnalyzer.CorpusMethods(c.Type))
            {
                bool exported = c.IsExported && m.DeclaredAccessibility == Accessibility.Public;
                bool write = behaviour.IsWrite(m);
                bool unused = exported && !behaviour.IsUsedExternally(m);
                For(c.Group).Method(exported, write, unused);
                all.Method(exported, write, unused);
            }
        }

        return new Result(
            acc.ToDictionary(kv => kv.Key, kv => kv.Value.Build(), StringComparer.OrdinalIgnoreCase),
            all.Build(),
            behaviour.Edges);
    }

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
