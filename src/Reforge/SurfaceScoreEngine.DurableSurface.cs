using Microsoft.CodeAnalysis;

namespace Reforge;

// Pass 1 — durable surface: what an assembly exports and therefore cannot change without
// breaking a consumer. DTO members, service/repository interface methods, controller actions.
public sealed partial class SurfaceScoreEngine
{
    private void ScoreDurableSurface(List<ClassifiedType> classified, ScoreReport report,
        HashSet<string> analyzedAssemblies)
    {
        // Types that get their own ScoreDtoSurface call. A DTO deriving from another scored DTO
        // must not be charged for the base's properties as well — the base already pays for them.
        var scoredDtos = new HashSet<string>(
            classified
                .Where(x => x.IsExported && x.Tags.Contains("dto") && LooksLikeDataCarrier(x.Type, analyzedAssemblies))
                .Select(x => SolutionClassifier.TypeKey(x.Type.OriginalDefinition)),
            StringComparer.Ordinal);

        foreach (var c in classified)
        {
            // Durable surface is what the assembly exports. An internal type's members cannot be
            // called from another section, so they are not API — no consumer can be broken by
            // changing them. Their implementation still scores on the internal-complexity axis.
            if (!c.IsExported) continue;

            if (c.Tags.Contains("dto") && LooksLikeDataCarrier(c.Type, analyzedAssemblies))
                ScoreDtoSurface(c, report, scoredDtos, analyzedAssemblies);

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
    private void ScoreDtoSurface(ClassifiedType c, ScoreReport report, HashSet<string> scoredDtos,
        HashSet<string> analyzedAssemblies)
    {
        AddEntry(report, c.Group, "publicDtoType", _config.Weight("publicDtoType"), c.Type, c.File, c.Line, null);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (INamedTypeSymbol? t = c.Type; t is not null; t = t.BaseType)
        {
            if (!ReferenceEquals(t, c.Type))
            {
                if (t.SpecialType == SpecialType.System_Object) break;
                if (!IsInAnalyzedSolution(t, analyzedAssemblies)) break;
                // OriginalDefinition: a constructed generic base (`BaseResponse<int>`) has a
                // different display string from the declaration the set was built from
                // (`BaseResponse<T>`), so querying with the constructed form misses and the
                // derived DTO pays a second time for properties the base already paid for.
                if (scoredDtos.Contains(SolutionClassifier.TypeKey(t.OriginalDefinition))) break;
            }

            foreach (var member in t.GetMembers())
            {
                if (member is not IPropertySymbol prop) continue;
                if (prop.DeclaredAccessibility != Accessibility.Public) continue;
                // Keyed on the signature, not the name. Indexer overloads (`this[int]`,
                // `this[string]`) are all named `Item`, and they are distinct published
                // properties — a name-only key would charge only the first. Ordinary properties
                // have no parameters, so an override or `new` declaration still collapses.
                if (!seen.Add(PropertyKey(prop))) continue;

                var loc = prop.Locations.FirstOrDefault(l => l.IsInSource);
                var (file, line) = LocateMember(loc, c);

                var (rule, weight) = ClassifyDtoProperty(prop, analyzedAssemblies);
                var detail = prop.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
                // Name the base so a reader is not left wondering why a type is charged for a
                // property its own declaration does not contain.
                if (!ReferenceEquals(t, c.Type)) detail += $" (inherited from {t.Name})";
                AddEntry(report, c.Group, rule, weight, prop, file, line, detail);
            }
        }
    }

    /// <summary>
    /// Whether a type belongs to the analysed solution, decided by declaring assembly rather than
    /// by source location. The two agree only for the common project layout — see the comment where
    /// the set is built in <see cref="ScoreAsync"/>.
    /// </summary>
    private static bool IsInAnalyzedSolution(ISymbol t, HashSet<string> analyzedAssemblies) =>
        t.ContainingAssembly?.Name is { } name && analyzedAssemblies.Contains(name);

    /// <summary>
    /// A public instance indexer. <see cref="CanonicalReadDtoSet.IsCarriedData"/> excludes these on
    /// purpose — an indexer is not a nameable fact, so it cannot become an inventory path — but
    /// <see cref="ScoreDtoSurface"/> does charge indexers as published properties, and a type whose
    /// only properties are indexers would otherwise be scored as nothing at all: not a data carrier,
    /// so no <c>publicDtoType</c>, and never reached, so no per-indexer charge either. Counting it
    /// here keeps the predicate and the scorer describing the same set.
    /// </summary>
    private static bool IsPublishedIndexer(ISymbol m) =>
        m is IPropertySymbol
        {
            IsStatic: false, Parameters.Length: > 0,
            DeclaredAccessibility: Accessibility.Public, GetMethod: not null
        };

    /// <summary>
    /// Identity of a published property for de-duplication across a base chain: its name plus, for
    /// an indexer, its parameter types. A derived declaration shadowing or overriding a base one
    /// collapses; two indexer overloads do not.
    /// </summary>
    private static string PropertyKey(IPropertySymbol prop) =>
        prop.Parameters.Length == 0
            ? prop.Name
            : $"{prop.Name}({string.Join(",", prop.Parameters.Select(p => p.Type.ToDisplayString()))})";

    private (string Rule, int Weight) ClassifyDtoProperty(IPropertySymbol prop, HashSet<string> analyzedAssemblies)
    {
        var t = prop.Type;
        if (IsCollectionType(t))
            return ("dtoCollectionProperty", _config.Weight("dtoCollectionProperty"));
        if (IsNestedDtoType(t, analyzedAssemblies))
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

    private static bool IsNestedDtoType(ITypeSymbol t, HashSet<string> analyzedAssemblies)
    {
        if (t.SpecialType != SpecialType.None) return false;
        if (t.TypeKind != TypeKind.Class && t.TypeKind != TypeKind.Struct) return false;
        if (t.ContainingNamespace?.ToDisplayString().StartsWith("System", StringComparison.Ordinal) == true) return false;
        // Same boundary as the base-chain walks, for the same reason: a solution type reached
        // through a compiled DLL reference has no source location, and treating it as foreign
        // would price a nested DTO property (3) as a scalar one (1).
        if (!IsInAnalyzedSolution(t, analyzedAssemblies)) return false;
        return true;
    }

    /// <summary>
    /// Whether a type is a pure data carrier: carried data, and nothing a consumer can invoke.
    /// A type only counts as a DTO if it carries data and no behaviour, which keeps static
    /// command-registration classes and services that happen to match a name pattern out of the
    /// DTO score.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Stated as an <b>allowlist</b> — is every member carried data, or invisible to a consumer? —
    /// rather than as a list of behaviour shapes to reject. The reject-list framing loses, and lost
    /// here four times in a row: ordinary methods, then inherited events, then non-abstract default
    /// interface methods, then explicit interface implementations (which Roslyn reports as
    /// <c>private</c> while anyone who casts can call them). Each miss silently published a
    /// behavioural type as DTO surface. Asking the closed question instead means an unrecognised
    /// member shape disqualifies by default, so the next one nobody thought of fails safe.
    /// <see cref="CanonicalReadDtoSet.IsCarriedData"/> and
    /// <see cref="CanonicalReadDtoSet.IsInvisibleToConsumers"/> are the shared answer.
    /// </para>
    /// <para>
    /// The one deliberate difference from <see cref="CanonicalReadDtoSet.IsDataCarrier"/>: this walk
    /// <b>stops at the solution boundary</b>. Letting it climb into framework bases measured +5
    /// points on Humans, all of it one EF migration class — <c>ExpenseLineProofRows : Migration</c> —
    /// admitted as published DTO surface because EF's <c>Migration</c> base declares public
    /// properties. A framework base's members are not this section's surface to withdraw. That the
    /// other predicate has no such stop is filed as its own issue rather than changed from here.
    /// </para>
    /// </remarks>
    private static bool LooksLikeDataCarrier(INamedTypeSymbol type, HashSet<string> analyzedAssemblies)
    {
        if (type.IsStatic) return false;
        if (type.TypeKind is not (TypeKind.Class or TypeKind.Struct)) return false;

        int props = 0;
        for (INamedTypeSymbol? t = type; t is not null; t = t.BaseType)
        {
            if (!ReferenceEquals(t, type))
            {
                // Object and ValueType carry universal members rather than a published API choice.
                if (t.SpecialType is SpecialType.System_Object or SpecialType.System_ValueType) break;
                if (!IsInAnalyzedSolution(t, analyzedAssemblies)) break;
            }

            foreach (var m in t.GetMembers())
            {
                if (CanonicalReadDtoSet.IsCarriedData(m) || IsPublishedIndexer(m)) { props++; continue; }
                if (CanonicalReadDtoSet.IsInvisibleToConsumers(m)) continue;
                return false;
            }
        }

        // A default interface method is behaviour the type never declares anywhere, so no walk over
        // declarations can see it. Only NON-abstract members count: an abstract one is either
        // implemented on the type (judged above) or unimplementable, and every record implements
        // IEquatable<T>, so counting abstract interface members would disqualify every record.
        foreach (var iface in type.AllInterfaces)
            foreach (var m in iface.GetMembers())
                if (m is { IsAbstract: false, IsStatic: false, DeclaredAccessibility: Accessibility.Public }
                    and (IMethodSymbol or IEventSymbol))
                    return false;

        return props >= 1;
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
