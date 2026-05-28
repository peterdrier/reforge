using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Reforge;

/// <summary>
/// Roslyn analysis of "boundary input" types — the parameter/command/request objects that
/// sit on a public service boundary. A parameter-object refactor can dodge
/// <c>methodParameterOverflow</c> by moving a long argument list into one of these, yet the
/// object still carries the same durable surface across the boundary even when its state is
/// hidden behind <c>internal</c>/<c>private</c> accessors. These helpers discover the real
/// carried state via the symbol model (widest constructor, fields/properties of any
/// accessibility, record positional members) so the scoring rules can charge for it.
/// </summary>
public static class BoundaryInput
{
    private static readonly string[] Suffixes = { "Input", "Request", "Command", "Options", "Parameters", "Args" };

    public static bool IsBoundaryName(string name)
        => Suffixes.Any(s => name.EndsWith(s, StringComparison.Ordinal));

    /// <summary>Parameter count of the widest non-trivial instance constructor.</summary>
    public static int WidestCtorParamCount(INamedTypeSymbol type)
        => type.InstanceConstructors.Select(c => c.Parameters.Length).DefaultIfEmpty(0).Max();

    /// <summary>
    /// The number of distinct data members the type carries. Uses the larger of the widest
    /// constructor's parameter count and the count of instance fields/properties (record
    /// positional members included), so a type that hides its state behind internal getters
    /// still reports its true breadth.
    /// </summary>
    public static int DataMemberCount(INamedTypeSymbol type)
    {
        int ctorMax = WidestCtorParamCount(type);
        int props = type.GetMembers().OfType<IPropertySymbol>()
            .Count(p => !p.IsStatic && !p.IsIndexer && !p.IsImplicitlyDeclared);
        int fields = type.GetMembers().OfType<IFieldSymbol>()
            .Count(f => !f.IsStatic && !f.IsConst && f.AssociatedSymbol is null && !f.IsImplicitlyDeclared);
        return Math.Max(ctorMax, props + fields);
    }

    /// <summary>Members whose value is publicly readable (public getter property or public field).</summary>
    public static int PublicReadableCount(INamedTypeSymbol type)
    {
        int props = type.GetMembers().OfType<IPropertySymbol>().Count(p =>
            !p.IsStatic && !p.IsIndexer
            && p.DeclaredAccessibility == Accessibility.Public
            && p.GetMethod is not null
            && p.GetMethod.DeclaredAccessibility == Accessibility.Public);
        int fields = type.GetMembers().OfType<IFieldSymbol>().Count(f =>
            !f.IsStatic && !f.IsConst && f.AssociatedSymbol is null && !f.IsImplicitlyDeclared
            && f.DeclaredAccessibility == Accessibility.Public);
        return props + fields;
    }

    /// <summary>
    /// True if the type has any user-defined public instance method — real behavior,
    /// validation, or a domain transition. Compiler-synthesized record members
    /// (Equals/GetHashCode/ToString/Deconstruct/Clone/PrintMembers) are implicitly declared
    /// and don't count; a hand-written <c>Validate()</c> does.
    /// </summary>
    public static bool HasBehavior(INamedTypeSymbol type)
    {
        foreach (var m in type.GetMembers().OfType<IMethodSymbol>())
        {
            if (m.MethodKind != MethodKind.Ordinary) continue;
            if (m.IsStatic || m.IsImplicitlyDeclared) continue;
            if (m.DeclaredAccessibility != Accessibility.Public) continue;
            return true;
        }
        return false;
    }

    /// <summary>
    /// True when the widest constructor is a pure assignment bag: it only copies parameters
    /// into members, with no branching, throws, or guard/validation calls. A record's
    /// synthesized primary constructor (no explicit body) counts as pure. A constructor that
    /// validates invariants is NOT a pure bag — that's meaningful behavior, so the type is
    /// treated as legitimate.
    /// </summary>
    public static bool CtorIsDirectAssignment(INamedTypeSymbol type, CancellationToken ct)
    {
        var ctor = type.InstanceConstructors
            .Where(c => c.Parameters.Length > 0)
            .OrderByDescending(c => c.Parameters.Length)
            .FirstOrDefault();
        if (ctor is null) return false;

        var sref = ctor.DeclaringSyntaxReferences.FirstOrDefault();
        if (sref is null) return true; // synthesized (record positional ctor) — pure data
        if (sref.GetSyntax(ct) is not ConstructorDeclarationSyntax cds) return true;

        var body = (SyntaxNode?)cds.Body ?? cds.ExpressionBody;
        if (body is null) return true;

        foreach (var n in body.DescendantNodes())
        {
            if (n is IfStatementSyntax or SwitchStatementSyntax or SwitchExpressionSyntax
                or ThrowStatementSyntax or ThrowExpressionSyntax
                or ForStatementSyntax or ForEachStatementSyntax or WhileStatementSyntax or DoStatementSyntax
                or ConditionalExpressionSyntax)
                return false;
            // Guard/validation invocations (ThrowIfNull, Validate, Require…) signal invariants.
            if (n is InvocationExpressionSyntax inv)
            {
                var name = inv.Expression switch
                {
                    MemberAccessExpressionSyntax ma => ma.Name.Identifier.Text,
                    IdentifierNameSyntax id => id.Identifier.Text,
                    _ => null
                };
                if (name is not null &&
                    (name.Contains("Throw", StringComparison.Ordinal)
                     || name.Contains("Validate", StringComparison.Ordinal)
                     || name.Contains("Require", StringComparison.Ordinal)
                     || name.Contains("Ensure", StringComparison.Ordinal)))
                    return false;
            }
        }
        return true;
    }
}
