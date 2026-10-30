using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Reforge;

/// <summary>
/// One declaration that carries code: a method, constructor, operator, finalizer, a property,
/// indexer or event with accessor bodies, or a field or field-like event whose initializer holds a
/// delegate.
/// </summary>
/// <param name="Symbol">The declared member.</param>
/// <param name="Location">Where the declaration's name is, for the report.</param>
/// <param name="Cognitive">Cognitive complexity of the declaration's own code, from its own root.</param>
/// <param name="Locals">
/// The own cognitive complexity of each local function and lambda held in a local variable declared
/// inside it. Each is a declaration too; its complexity is already part of <paramref name="Cognitive"/>.
/// </param>
internal sealed record ContentsUnit(ISymbol Symbol, Location Location, CognitiveScore Cognitive, IReadOnlyList<int> Locals)
{
    public int LocalFunctions => Locals.Count;
}

/// <summary>
/// Prices a section's contents as a census: every declaration that carries code pays a flat
/// <c>methodCount</c> charge, and its own cognitive complexity through <see cref="Curve"/>, which is
/// linear up to a threshold and taxed above it. See
/// <c>docs/superpowers/specs/2026-08-21-section-contents-pricing-design.md</c>.
/// </summary>
/// <remarks>
/// <para>Every declaration is charged from its own root, whatever its accessibility, so widening a
/// helper or moving it to another type changes nothing here; visibility is priced on the surface
/// axis only.</para>
/// <para>Below the threshold complexity costs the same wherever it sits, so the total logic is priced
/// independent of how it is arranged, and only the flat charge moves: splitting a small method costs
/// <c>m</c> per piece and saves nothing, and merging small unreused pieces saves <c>m</c>. Above
/// the threshold the tax grows faster than the complexity, so a large method is cheapest split into
/// pieces near the threshold. The two pressures meet there: huge methods get split, tiny ones get
/// merged.</para>
/// <para>Code moved into another kind of declaration is still a declaration. A computed property, a
/// delegate-valued field, a local function and a lambda held in a local each count, so turning a
/// helper into one of them is not a way out of the count; a local helper's complexity stays with
/// the member it sits in, where the walk already charges it.</para>
/// </remarks>
internal static class ContentsPricing
{
    /// <summary>
    /// <c>f(n) = n + tax·max(0, n − threshold)^exponent</c>: linear up to the threshold, convex past
    /// it, never below <c>n</c>.
    /// </summary>
    public static double Curve(int complexity, int threshold, int taxPercent, double exponent)
        => complexity <= 0 ? 0
            : complexity + taxPercent / 100.0 * Math.Pow(Math.Max(0, complexity - threshold), exponent);

    /// <summary>The handwritten declarations in <paramref name="type"/> that carry code.</summary>
    public static List<ContentsUnit> Units(INamedTypeSymbol type, CancellationToken ct)
    {
        var units = new List<ContentsUnit>();
        foreach (var member in type.GetMembers())
        {
            if (member.IsImplicitlyDeclared) continue;
            var code = member switch
            {
                // Accessors are priced with the property or event they belong to.
                IMethodSymbol { AssociatedSymbol: null } m => MethodCode(m, ct),
                IPropertySymbol or IEventSymbol => AccessorCode(member, ct),
                IFieldSymbol f => DelegateInitializer(f, ct),
                _ => null
            };
            if (code is null || code.Count == 0) continue;

            int score = 0, nested = 0, nestedLine = 0;
            var locals = new List<int>();
            foreach (var node in code)
            {
                var c = ImplementationComplexity.CognitiveOf(node);
                score += c.Score;
                if (c.NestedScore > nested) (nested, nestedLine) = (c.NestedScore, c.NestedLine);
                foreach (var local in node.DescendantNodesAndSelf())
                    if (LocalDeclarationCode(local) is { } localCode)
                        locals.Add(ImplementationComplexity.CognitiveOf(localCode).Score);
            }
            var tree = code[0].SyntaxTree;
            var location = member.Locations.FirstOrDefault(l => l.SourceTree == tree) ?? code[0].GetLocation();
            units.Add(new ContentsUnit(member, location, new CognitiveScore(score, nested, nestedLine), locals));
        }
        return units;
    }

    /// <summary>
    /// The body a developer wrote. A partial method's body hangs off its implementation part, and a
    /// partial type can list a bodyless or generated declaration first.
    /// </summary>
    private static List<SyntaxNode>? MethodCode(IMethodSymbol m, CancellationToken ct)
    {
        foreach (var r in (m.PartialImplementationPart ?? m).DeclaringSyntaxReferences)
        {
            if (GeneratedCode.IsGeneratedFile(r.SyntaxTree.FilePath)) continue;
            if (r.GetSyntax(ct) is not BaseMethodDeclarationSyntax bm) continue;
            if (bm.Body is { } body) return new List<SyntaxNode> { body };
            if (bm.ExpressionBody is { } arrow) return new List<SyntaxNode> { arrow };
        }
        return null;
    }

    /// <summary>
    /// Accessor bodies, an arrow body, or a delegate in a property or field-like event initializer.
    /// Auto-accessors carry no code.
    /// </summary>
    private static List<SyntaxNode>? AccessorCode(ISymbol member, CancellationToken ct)
    {
        var code = new List<SyntaxNode>();
        foreach (var r in member.DeclaringSyntaxReferences)
        {
            if (GeneratedCode.IsGeneratedFile(r.SyntaxTree.FilePath)) continue;
            switch (r.GetSyntax(ct))
            {
                case PropertyDeclarationSyntax { ExpressionBody: { } arrow }:
                    code.Add(arrow);
                    continue;
                case IndexerDeclarationSyntax { ExpressionBody: { } arrow }:
                    code.Add(arrow);
                    continue;
                case BasePropertyDeclarationSyntax bp:
                    foreach (var a in bp.AccessorList?.Accessors ?? default)
                    {
                        if (a.Body is { } body) code.Add(body);
                        else if (a.ExpressionBody is { } ab) code.Add(ab);
                    }
                    if (bp is PropertyDeclarationSyntax { Initializer.Value: { } init } && HoldsDelegate(init))
                        code.Add(init);
                    continue;
                // A field-like event declares through a variable declarator, as a field does.
                case VariableDeclaratorSyntax { Initializer.Value: { } eventInit } when HoldsDelegate(eventInit):
                    code.Add(eventInit);
                    continue;
            }
        }
        return code;
    }

    /// <summary>
    /// A field whose initializer is a lambda or anonymous method is a method in all but name;
    /// without this, moving a body into one would take it out of both the count and the curve.
    /// </summary>
    private static List<SyntaxNode>? DelegateInitializer(IFieldSymbol f, CancellationToken ct)
    {
        foreach (var r in f.DeclaringSyntaxReferences)
        {
            if (GeneratedCode.IsGeneratedFile(r.SyntaxTree.FilePath)) continue;
            if (r.GetSyntax(ct) is VariableDeclaratorSyntax { Initializer.Value: { } init } && HoldsDelegate(init))
                return new List<SyntaxNode> { init };
        }
        return null;
    }

    /// <summary>
    /// A local function, or a lambda held in a local variable: a named helper either way, so turning
    /// a private method into one is not a way out of the count. A lambda passed straight to a call
    /// (the LINQ case) is an argument, not a declaration. Returns the declaration's own code, or null.
    /// </summary>
    private static SyntaxNode? LocalDeclarationCode(SyntaxNode n) => n switch
    {
        LocalFunctionStatementSyntax f => (SyntaxNode?)f.Body ?? f.ExpressionBody,
        VariableDeclaratorSyntax { Initializer.Value: AnonymousFunctionExpressionSyntax lambda, Parent.Parent: LocalDeclarationStatementSyntax }
            => lambda.Body,
        _ => null,
    };

    private static bool HoldsDelegate(ExpressionSyntax init)
        => init.DescendantNodesAndSelf().OfType<AnonymousFunctionExpressionSyntax>().Any();
}

/// <summary>
/// What the contents census counted: declarations by accessibility and by own-complexity band.
/// Informational. It is the measurement the census weights are tuned against: declarations times
/// <c>methodCount</c> is the count term, and the bands show how much of the corpus the curve's
/// convexity reaches.
/// </summary>
public sealed class ContentsCensus
{
    /// <summary>Upper bounds of the complexity bands; the last band is everything above the last bound.</summary>
    public static readonly int[] BandUpperBounds = { 0, 4, 9, 19, 49 };

    /// <summary>A fresh empty census; one per call, since a census is filled in place.</summary>
    public static ContentsCensus Empty => new();

    public int Declarations => Public + Internal + Protected + Private + LocalFunctions;
    public int Public { get; private set; }
    public int Internal { get; private set; }
    public int Protected { get; private set; }
    public int Private { get; private set; }
    public int LocalFunctions { get; private set; }

    /// <summary>Sum of every declaration's own cognitive complexity.</summary>
    public int Cognitive { get; private set; }

    /// <summary>Declarations per band of <see cref="BandUpperBounds"/>, plus one for the open top band.</summary>
    public int[] Bands { get; } = new int[BandUpperBounds.Length + 1];

    internal void Add(ContentsUnit unit)
    {
        switch (unit.Symbol.DeclaredAccessibility)
        {
            case Accessibility.Public: Public++; break;
            case Accessibility.Internal or Accessibility.ProtectedOrInternal: Internal++; break;
            case Accessibility.Protected or Accessibility.ProtectedAndInternal: Protected++; break;
            default: Private++; break;
        }
        LocalFunctions += unit.LocalFunctions;
        Cognitive += unit.Cognitive.Score;
        // A member is banded by the complexity it is charged for; each local declaration in it by
        // its own, so the bands count every declaration once.
        AddToBand(unit.Cognitive.Score);
        foreach (var local in unit.Locals) AddToBand(local);
    }

    private void AddToBand(int score)
    {
        int band = Array.FindIndex(BandUpperBounds, b => score <= b);
        Bands[band < 0 ? BandUpperBounds.Length : band]++;
    }

    /// <summary>Band labels, aligned with <see cref="Bands"/>: <c>0</c>, <c>1-4</c>, … <c>50+</c>.</summary>
    public static IReadOnlyList<string> BandLabels { get; } = BuildLabels();

    private static List<string> BuildLabels()
    {
        var labels = new List<string>();
        int lo = 0;
        foreach (var hi in BandUpperBounds)
        {
            labels.Add(lo == hi ? $"{hi}" : $"{lo}-{hi}");
            lo = hi + 1;
        }
        labels.Add($"{lo}+");
        return labels;
    }
}
