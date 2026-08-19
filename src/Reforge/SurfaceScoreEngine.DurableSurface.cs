using Microsoft.CodeAnalysis;

namespace Reforge;

// Pass 1 — durable surface: what an assembly exports and therefore cannot change without
// breaking a consumer. DTO members, service/repository interface methods, controller actions.
public sealed partial class SurfaceScoreEngine
{
    private void ScoreDurableSurface(List<ClassifiedType> classified, ScoreReport report)
    {
        // Types that get their own ScoreDtoSurface call. A DTO deriving from another scored DTO
        // must not be charged for the base's properties as well — the base already pays for them.
        var scoredDtos = new HashSet<string>(
            classified
                .Where(x => x.IsExported && x.Tags.Contains("dto") && LooksLikeDataCarrier(x.Type))
                .Select(x => SolutionClassifier.TypeKey(x.Type)),
            StringComparer.Ordinal);

        foreach (var c in classified)
        {
            // Durable surface is what the assembly exports. An internal type's members cannot be
            // called from another section, so they are not API — no consumer can be broken by
            // changing them. Their implementation still scores on the internal-complexity axis.
            if (!c.IsExported) continue;

            if (c.Tags.Contains("dto") && LooksLikeDataCarrier(c.Type))
                ScoreDtoSurface(c, report, scoredDtos);

            if (c.Tags.Contains("readServiceInterface"))
                ScoreInterfaceMethods(c, "readServiceInterfaceMethod", report);
            else if (c.Tags.Contains("fullServiceInterface"))
                ScoreInterfaceMethods(c, "fullServiceInterfaceMethod", report);
            else if (c.Tags.Contains("repositoryInterface"))
            {
                AddEntry(report, c.Group, "newRepositoryInterface",
                    _config.Weight("newRepositoryInterface"), c.Type, c.File, c.Line, null);
                ScoreInterfaceMethods(c, "repositoryInterfaceMethod", report);
            }
            else if (c.Tags.Contains("repositoryImplementation"))
            {
                AddEntry(report, c.Group, "newRepositoryImplementation",
                    _config.Weight("newRepositoryImplementation"), c.Type, c.File, c.Line, null);
                ScorePublicMethods(c, "repositoryImplementationMethod", report);
            }
            else if (c.Tags.Contains("controller"))
                ScoreControllerActions(c, report);
            else if (c.Tags.Contains("backgroundJob"))
                AddEntry(report, c.Group, "backgroundJob",
                    _config.Weight("backgroundJob"), c.Type, c.File, c.Line, null);
            else if (c.Tags.Contains("applicationService"))
                ScorePublicMethods(c, "applicationServiceMethod", report);
        }
    }

    /// <summary>
    /// Charges a DTO's published shape: the type itself, and every public property a consumer can
    /// read off it — declared on the type or <b>inherited</b>.
    /// </summary>
    /// <remarks>
    /// Inherited properties are charged because they are published just as surely as declared ones,
    /// and scoring only <c>GetMembers()</c> (which does not return inherited members) made the
    /// charge avoidable by moving the properties up to a base class whose name matches no DTO
    /// pattern. Nothing about the exported shape changes under that edit — see issue #29 (3b).
    /// <para>
    /// The walk stops at three places, each for its own reason: at <c>object</c>; at the first base
    /// declared outside the solution, because a framework base's properties are not this section's
    /// surface to withdraw; and at a base that is itself a separately scored DTO, which already
    /// pays for its own properties. A property redeclared in a derived type is charged once.
    /// </para>
    /// </remarks>
    private void ScoreDtoSurface(ClassifiedType c, ScoreReport report, HashSet<string> scoredDtos)
    {
        AddEntry(report, c.Group, "publicDtoType", _config.Weight("publicDtoType"), c.Type, c.File, c.Line, null);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (INamedTypeSymbol? t = c.Type; t is not null; t = t.BaseType)
        {
            if (!ReferenceEquals(t, c.Type))
            {
                if (t.SpecialType == SpecialType.System_Object) break;
                if (!t.Locations.Any(l => l.IsInSource)) break;
                if (scoredDtos.Contains(SolutionClassifier.TypeKey(t))) break;
            }

            foreach (var member in t.GetMembers())
            {
                if (member is not IPropertySymbol prop) continue;
                if (prop.DeclaredAccessibility != Accessibility.Public) continue;
                if (!seen.Add(prop.Name)) continue;

                var loc = prop.Locations.FirstOrDefault(l => l.IsInSource);
                var (file, line) = LocateMember(loc, c);

                var (rule, weight) = ClassifyDtoProperty(prop);
                var detail = prop.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
                // Name the base so a reader is not left wondering why a type is charged for a
                // property its own declaration does not contain.
                if (!ReferenceEquals(t, c.Type)) detail += $" (inherited from {t.Name})";
                AddEntry(report, c.Group, rule, weight, prop, file, line, detail);
            }
        }
    }

    private (string Rule, int Weight) ClassifyDtoProperty(IPropertySymbol prop)
    {
        var t = prop.Type;
        if (IsCollectionType(t))
            return ("dtoCollectionProperty", _config.Weight("dtoCollectionProperty"));
        if (IsNestedDtoType(t))
            return ("dtoNestedProperty", _config.Weight("dtoNestedProperty"));
        return ("dtoScalarProperty", _config.Weight("dtoScalarProperty"));
    }

    private static bool IsCollectionType(ITypeSymbol t)
    {
        if (t is IArrayTypeSymbol) return true;
        if (t is INamedTypeSymbol n)
        {
            var n2 = n.OriginalDefinition.ToDisplayString();
            return n2.StartsWith("System.Collections.Generic.", StringComparison.Ordinal)
                || n2 == "System.Collections.IEnumerable"
                || n2.StartsWith("System.Collections.Immutable.", StringComparison.Ordinal);
        }
        return false;
    }

    private static bool IsNestedDtoType(ITypeSymbol t)
    {
        if (t.SpecialType != SpecialType.None) return false;
        if (t.TypeKind != TypeKind.Class && t.TypeKind != TypeKind.Struct) return false;
        if (t.ContainingNamespace?.ToDisplayString().StartsWith("System", StringComparison.Ordinal) == true) return false;
        if (!t.Locations.Any(l => l.IsInSource)) return false;
        return true;
    }

    /// <summary>
    /// A type only counts as a DTO if it actually carries data: public properties and
    /// no business-logic methods. This prevents static command-registration classes,
    /// service classes that happen to match a name pattern, etc. from inflating the DTO score.
    /// </summary>
    /// <summary>
    /// Whether a type is a pure data carrier: public properties and no behaviour.
    /// </summary>
    /// <remarks>
    /// Counted over the type <b>and its solution-declared base chain</b>. Counting only the type's
    /// own members left a cheaper version of the hole #29 (3b) describes: hoisting every property
    /// to a base drops the type's own property count to zero, it stops looking like a data carrier
    /// at all, and the <c>publicDtoType</c> charge disappears along with the per-property ones —
    /// while the published shape is identical. Inherited behaviour disqualifies a type for the same
    /// reason declared behaviour does: a consumer can call it.
    /// </remarks>
    private static bool LooksLikeDataCarrier(INamedTypeSymbol type)
    {
        if (type.IsStatic) return false;
        if (type.TypeKind is not (TypeKind.Class or TypeKind.Struct)) return false;

        int publicProps = 0;
        int publicMethods = 0;
        for (INamedTypeSymbol? t = type; t is not null; t = t.BaseType)
        {
            if (!ReferenceEquals(t, type))
            {
                if (t.SpecialType == SpecialType.System_Object) break;
                if (!t.Locations.Any(l => l.IsInSource)) break;
            }

            foreach (var m in t.GetMembers())
            {
                if (m.IsImplicitlyDeclared) continue;
                if (m.DeclaredAccessibility != Accessibility.Public) continue;

                switch (m)
                {
                    case IPropertySymbol:
                        publicProps++;
                        break;
                    case IMethodSymbol method
                        when method.MethodKind == MethodKind.Ordinary
                          && method.AssociatedSymbol is null:
                        publicMethods++;
                        break;
                }
            }
        }
        return publicProps >= 1 && publicMethods == 0;
    }

    private void ScoreInterfaceMethods(ClassifiedType c, string ruleKey, ScoreReport report)
    {
        var weight = _config.Weight(ruleKey);
        foreach (var member in c.Type.GetMembers())
        {
            if (member is not IMethodSymbol m) continue;
            if (m.MethodKind != MethodKind.Ordinary) continue;
            if (m.AssociatedSymbol is not null) continue;
            if (m.IsImplicitlyDeclared) continue;
            // Interface members are implicitly public, but C# 8 allows private/internal ones —
            // those are implementation detail of the interface, not part of what it exports.
            if (m.DeclaredAccessibility != Accessibility.Public) continue;

            var loc = m.Locations.FirstOrDefault(l => l.IsInSource);
            var (file, line) = LocateMember(loc, c);
            AddEntry(report, c.Group, ruleKey, weight, m, file, line, m.Name);
        }
    }

    private void ScorePublicMethods(ClassifiedType c, string ruleKey, ScoreReport report)
    {
        var weight = _config.Weight(ruleKey);
        foreach (var member in c.Type.GetMembers())
        {
            if (member is not IMethodSymbol m) continue;
            if (m.MethodKind != MethodKind.Ordinary) continue;
            if (m.AssociatedSymbol is not null) continue;
            if (m.IsImplicitlyDeclared) continue;
            if (m.DeclaredAccessibility != Accessibility.Public) continue;

            var loc = m.Locations.FirstOrDefault(l => l.IsInSource);
            var (file, line) = LocateMember(loc, c);
            AddEntry(report, c.Group, ruleKey, weight, m, file, line, m.Name);
        }
    }

    private void ScoreControllerActions(ClassifiedType c, ScoreReport report)
    {
        var weight = _config.Weight("controllerAction");
        foreach (var member in c.Type.GetMembers())
        {
            if (member is not IMethodSymbol m) continue;
            if (m.MethodKind != MethodKind.Ordinary) continue;
            if (m.DeclaredAccessibility != Accessibility.Public) continue;
            if (m.IsImplicitlyDeclared) continue;

            var loc = m.Locations.FirstOrDefault(l => l.IsInSource);
            var (file, line) = LocateMember(loc, c);
            AddEntry(report, c.Group, "controllerAction", weight, m, file, line, m.Name);
        }
    }
}
