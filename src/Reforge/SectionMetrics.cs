using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Reforge;

/// <summary>
/// An avg/p95/max distribution over a per-method metric, with the method that holds the max.
/// p95 is nearest-rank over the same sample the average is taken from.
/// </summary>
public sealed record MetricDistribution(double Avg, int P95, int Max, string MaxMethod)
{
    public static readonly MetricDistribution Empty = new(0, 0, 0, "");
}

/// <summary>
/// Size and complexity of one section's scored corpus — the context a score number lacks on its
/// own. A section's surface points can fall because its API shrank or because its code did; a
/// section's internal-complexity points can fall because methods got simpler or because they were
/// deleted. Neither is visible from the score, and most internal-complexity points are satisfiable
/// by edits that move no code (see issue #19), so the score is reported beside the size it describes.
/// </summary>
/// <remarks>
/// Informational only: nothing here feeds the score, and adding a metric can never change a total.
/// </remarks>
public sealed record SectionMetrics(
    int LocProd,
    int Files,
    int Classes,
    int Interfaces,
    /// <summary>Methods and constructors with a body — the sample both distributions are taken over.</summary>
    int Methods,
    MetricDistribution Cognitive,
    MetricDistribution Cyclomatic,
    /// <summary>
    /// Largest class or struct in the section, summed across partial declarations. This is the set
    /// <see cref="Classes"/> counts — deliberately <b>wider</b> than the set the <c>largeClass</c>
    /// rule scores, which is only the size-tracked roles (application service, repository
    /// implementation, controller, background job). A metric that mirrored the rule would, on 6 of
    /// Humans' 44 sections, report a smaller class than the section's real maximum and hide things
    /// like a 557-line API client or a 502-line content holder — which is size the section carries
    /// whether or not a rule currently charges for it.
    /// </summary>
    int MaxClassLoc,
    string MaxClassLocName)
{
    public static readonly SectionMetrics Empty =
        new(0, 0, 0, 0, 0, MetricDistribution.Empty, MetricDistribution.Empty, 0, "");
}

/// <summary>
/// Re-aggregates the size/complexity passes <c>snapshot</c> runs solution-wide by section instead.
/// </summary>
/// <remarks>
/// The corpus is deliberately the <b>scoring</b> corpus — the same <see cref="ClassifiedType"/>
/// list the rules run over — not the solution's whole file set, so a metric and a score always
/// describe the same code. Three consequences worth knowing:
/// <list type="bullet">
///   <item>Test projects are absent (the classifier never admits them), so there is no test LOC
///         here. Attributing tests to a section needs project-reference resolution, which is a
///         different problem.</item>
///   <item>Generated code (EF migrations, <c>*.g.cs</c>, <c>*.Designer.cs</c>) is excluded, exactly
///         as it is from the internal-complexity axis: it is not developer-controlled implementation,
///         and its huge generated methods would swamp every distribution.</item>
///   <item>Complexity is measured over methods that have a body. An abstract or interface
///         declaration carries no implementation, and folding a zero (or a 1) in for each would
///         drag a section's averages toward whichever number the bodyless case produced.</item>
/// </list>
/// </remarks>
public static class SectionMetricsAnalyzer
{
    /// <summary>
    /// Metrics per section plus the solution-level rollup. The rollup is computed over the pooled
    /// sample, not by averaging the sections' averages — a two-method section would otherwise weigh
    /// as much as a two-thousand-method one.
    /// </summary>
    public static (Dictionary<string, SectionMetrics> BySection, SectionMetrics Solution) Analyze(
        IReadOnlyList<ClassifiedType> classified, CancellationToken ct)
    {
        var bySection = new Dictionary<string, Accumulator>(StringComparer.OrdinalIgnoreCase);
        var solution = new Accumulator();
        // Non-blank lines per file, computed once. The same file is reached through every project
        // that references its assembly, and a partial type pulls in trees the primary location
        // never names, so both the count and the dedup have to key on the path.
        var locByFile = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var c in classified)
        {
            if (ct.IsCancellationRequested) break;

            var facts = Measure(c, locByFile, ct);
            // Every declaration was generated — the type contributes nothing to measure. Checked
            // on the facts rather than up front: a partial type is one symbol spanning several
            // files, and the classifier's primary file is only one of them.
            if (facts is null) continue;

            if (!bySection.TryGetValue(c.Group, out var acc))
            {
                acc = new Accumulator();
                bySection[c.Group] = acc;
            }
            acc.Feed(facts);
            solution.Feed(facts);
        }

        return (
            bySection.ToDictionary(kv => kv.Key, kv => kv.Value.Build(), StringComparer.OrdinalIgnoreCase),
            solution.Build());
    }

    // ---------------- Per-type measurement ----------------

    private sealed record MethodFact(string Name, int Cognitive, int Cyclomatic);

    private sealed record TypeFacts(
        string Name,
        List<(string Path, int Loc)> Files,
        bool IsClass,
        bool IsInterface,
        int ClassLoc,
        List<MethodFact> Methods);

    /// <summary>
    /// Measures one type, or returns null when every declaration of it is generated.
    /// </summary>
    /// <remarks>
    /// Generated-ness is decided <b>per declaration</b>, not once from the classifier's primary
    /// file. A partial type is a single symbol spanning several files, and a handwritten class with
    /// a generated <c>.Designer.cs</c> half has one of each: filtering on the primary file alone
    /// would leak the generated half's LOC and methods in whenever the handwritten file happened to
    /// be primary, and discard the handwritten half whenever the generated one was. Methods are
    /// filtered by their own declaring tree for the same reason.
    /// </remarks>
    private static TypeFacts? Measure(ClassifiedType c, Dictionary<string, int> locByFile, CancellationToken ct)
    {
        var files = new List<(string Path, int Loc)>();
        int classLoc = 0;
        bool anyDeclaration = false;

        foreach (var reference in c.Type.DeclaringSyntaxReferences)
        {
            var path = reference.SyntaxTree.FilePath;
            if (IsGeneratedFile(path)) continue;
            anyDeclaration = true;

            if (!string.IsNullOrEmpty(path))
            {
                if (!locByFile.TryGetValue(path, out var loc))
                {
                    loc = CountNonBlankLines(reference.SyntaxTree, ct);
                    locByFile[path] = loc;
                }
                files.Add((path, loc));
            }

            // Partial declarations sum, matching the largeClass rule's own measurement.
            if (reference.GetSyntax(ct) is TypeDeclarationSyntax declaration)
                classLoc += ImplementationComplexity.NonBlankLines(declaration);
        }

        if (!anyDeclaration) return null;

        var methods = new List<MethodFact>();
        foreach (var member in c.Type.GetMembers())
        {
            if (member is not IMethodSymbol m) continue;
            // Anything that declares a body counts, stated as an exclusion rather than an allowlist
            // of MethodKinds. An allowlist has to be right about every kind that can carry an
            // implementation — ordinary methods, constructors, static constructors, explicit
            // interface implementations, operators, finalizers — and each one it misses silently
            // drops real code out of both distributions with no signal that anything is absent.
            // Two exclusions, both "not written implementation" rather than "not interesting":
            // property/event accessors (their bodies belong to the property, and `AssociatedSymbol`
            // is what marks them), and compiler-synthesized members.
            if (m.AssociatedSymbol is not null) continue;
            if (m.IsImplicitlyDeclared) continue;

            var syntax = MethodSyntax(m, ct);
            if (syntax is null) continue;
            if (IsGeneratedFile(syntax.SyntaxTree.FilePath)) continue;
            if (syntax.Body is null && syntax.ExpressionBody is null) continue;

            methods.Add(new MethodFact(
                MethodDisplay(c.Type.Name, m),
                ImplementationComplexity.Cognitive(syntax),
                ImplementationComplexity.Cyclomatic(syntax)));
        }

        return new TypeFacts(
            c.Type.Name,
            files,
            c.Type.TypeKind is TypeKind.Class or TypeKind.Struct,
            c.Type.TypeKind == TypeKind.Interface,
            classLoc,
            methods);
    }

    /// <summary>
    /// Snapshot-style display name: <c>Type.Method</c>, with <c>Type.ctor</c> / <c>Type.cctor</c>
    /// for constructors. An explicitly implemented interface method carries its interface in
    /// <see cref="ISymbol.Name"/> already (<c>IFoo.Run</c>), so the default arm reads correctly.
    /// </summary>
    private static string MethodDisplay(string typeName, IMethodSymbol m) => m.MethodKind switch
    {
        MethodKind.Constructor => $"{typeName}.ctor",
        MethodKind.StaticConstructor => $"{typeName}.cctor",
        _ => $"{typeName}.{m.Name}"
    };

    /// <summary>
    /// The declaration that actually carries the body.
    /// </summary>
    /// <remarks>
    /// Two ways the first declaration is the wrong one. A <b>partial method</b> is two symbols: the
    /// defining declaration <c>partial void M();</c> is what <c>GetMembers()</c> returns and it has
    /// no body, while the implementation hangs off <see cref="IMethodSymbol.PartialImplementationPart"/> —
    /// so taking the first reference on the symbol in hand dropped every implemented partial method
    /// from the corpus entirely. A <b>partial type</b> can also declare the same method across files
    /// in an order that puts a bodyless reference first. Preferring the declaration with a body
    /// covers both, and falls back to the first so a genuinely bodyless method still resolves (and
    /// is then filtered by the caller).
    /// </remarks>
    private static BaseMethodDeclarationSyntax? MethodSyntax(IMethodSymbol m, CancellationToken ct)
    {
        var target = m.PartialImplementationPart ?? m;
        BaseMethodDeclarationSyntax? first = null;
        foreach (var r in target.DeclaringSyntaxReferences)
        {
            if (r.GetSyntax(ct) is not BaseMethodDeclarationSyntax bm) continue;
            first ??= bm;
            if (bm.Body is not null || bm.ExpressionBody is not null) return bm;
        }
        return first;
    }

    private static int CountNonBlankLines(SyntaxTree tree, CancellationToken ct)
    {
        int n = 0;
        foreach (var line in tree.GetText(ct).Lines)
            if (!string.IsNullOrWhiteSpace(line.ToString())) n++;
        return n;
    }

    /// <summary>
    /// Same exclusion the internal-complexity pass applies, kept in step with it deliberately:
    /// a metric that counted code the axis refuses to score would report growth the score cannot
    /// explain.
    /// </summary>
    private static bool IsGeneratedFile(string file)
    {
        if (string.IsNullOrEmpty(file)) return false;
        var f = file.Replace('\\', '/');
        return f.Contains("/Migrations/", StringComparison.OrdinalIgnoreCase)
            || f.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)
            || f.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase)
            || f.EndsWith(".generated.cs", StringComparison.OrdinalIgnoreCase);
    }

    // ---------------- Accumulation ----------------

    private sealed class Accumulator
    {
        private readonly HashSet<string> _files = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<int> _cognitive = new();
        private readonly List<int> _cyclomatic = new();
        private int _loc;
        private int _classes;
        private int _interfaces;
        private int _maxCognitive;
        private string _maxCognitiveMethod = "";
        private int _maxCyclomatic;
        private string _maxCyclomaticMethod = "";
        private int _maxClassLoc;
        private string _maxClassLocName = "";

        public void Feed(TypeFacts f)
        {
            foreach (var (path, loc) in f.Files)
                if (_files.Add(path)) _loc += loc;

            if (f.IsClass) _classes++;
            else if (f.IsInterface) _interfaces++;

            // Classes and structs only — the same set `classes` counts. `snapshot`'s solution-wide
            // MaxClassLoc measures every type declaration, interfaces included, which is how a
            // section whose largest declaration is an interface ends up reporting it under a field
            // called maxClassLoc. Note this is wider than the largeClass rule's own set (size-tracked
            // roles only): the block describes the section's size, and every other field in it is
            // corpus-wide, so narrowing this one to a rule's scope would make it the odd one out
            // and would hide a section's largest class whenever that class is not a tracked role.
            if (f.IsClass && f.ClassLoc > _maxClassLoc)
            {
                _maxClassLoc = f.ClassLoc;
                _maxClassLocName = f.Name;
            }

            foreach (var m in f.Methods)
            {
                _cognitive.Add(m.Cognitive);
                _cyclomatic.Add(m.Cyclomatic);
                // Set on the first sample, then only on a strict improvement. A section of
                // straight-line code scores 0 everywhere, and `0 > 0` never fires — so a strict
                // comparison alone left a non-empty distribution claiming max 0 held by no method,
                // which is not what the field promises. Ties go to the first method seen, which is
                // deterministic because the classified corpus is.
                if (_maxCognitiveMethod.Length == 0 || m.Cognitive > _maxCognitive)
                {
                    _maxCognitive = m.Cognitive;
                    _maxCognitiveMethod = m.Name;
                }
                if (_maxCyclomaticMethod.Length == 0 || m.Cyclomatic > _maxCyclomatic)
                {
                    _maxCyclomatic = m.Cyclomatic;
                    _maxCyclomaticMethod = m.Name;
                }
            }
        }

        public SectionMetrics Build()
        {
            _cognitive.Sort();
            _cyclomatic.Sort();
            return new SectionMetrics(
                LocProd: _loc,
                Files: _files.Count,
                Classes: _classes,
                Interfaces: _interfaces,
                Methods: _cognitive.Count,
                Cognitive: Distribution(_cognitive, _maxCognitive, _maxCognitiveMethod),
                Cyclomatic: Distribution(_cyclomatic, _maxCyclomatic, _maxCyclomaticMethod),
                MaxClassLoc: _maxClassLoc,
                MaxClassLocName: _maxClassLocName);
        }

        private static MetricDistribution Distribution(List<int> sorted, int max, string maxMethod)
        {
            if (sorted.Count == 0) return MetricDistribution.Empty;
            // Two decimals: the average is read as context beside an integer score, and more
            // precision than that is noise in every consumer's diff.
            var avg = Math.Round(sorted.Average(), 2);
            return new MetricDistribution(avg, StructuralAnalysis.Percentile(sorted, 0.95), max, maxMethod);
        }
    }
}
