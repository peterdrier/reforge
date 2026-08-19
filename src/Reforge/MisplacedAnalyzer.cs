using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.CSharp;

namespace Reforge;

/// <summary>What the analyzer concluded about one method's placement.</summary>
public enum MisplacedVerdict
{
    /// <summary>Almost entirely another section's behavior, and that section has no namesake. Move it.</summary>
    Move,

    /// <summary>Same shape as <see cref="Move"/>, but the target already declares this name — read both first.</summary>
    MoveWouldDuplicate,

    /// <summary>
    /// Concentrated on a section the whole solution depends on and which depends on none of it. "Move it
    /// into the foundation" is not advice; a thin wrapper over shared infrastructure is what this is.
    /// </summary>
    FoundationTarget,

    /// <summary>
    /// Reaches two or more other sections, so no single section could host it. Reported anyway, because a
    /// genuine orchestrator and an accidental junction drawer look identical from here.
    /// </summary>
    Orchestrator,

    /// <summary>
    /// Concentrated on another section, but reading its data carriers rather than calling its behavior.
    /// Usually a mapper, which belongs on the consuming side.
    /// </summary>
    Mapper,

    /// <summary>Concentrated on another section, but pinned in place: the contract would have to move too.</summary>
    Blocked
}

/// <summary>One method whose body suggests it is in the wrong section.</summary>
public sealed record MisplacedMethod(
    string Method,
    string File,
    int Line,
    string Section,
    string? TargetSection,
    int OwnTouches,
    int TargetBehaviorTouches,
    int TargetDataTouches,
    IReadOnlyList<string> SectionsTouched,
    MisplacedVerdict Verdict,
    string Evidence,
    string? DuplicateOf,
    string? BlockedBy,
    /// <summary>
    /// The concrete type in <see cref="TargetSection"/> this method leans on hardest — the natural host if
    /// it moves. Null when no destination was chosen. A section is not a place a method goes.
    /// </summary>
    string? DestinationType);

/// <summary>How much of the solution depends on a section, and how much of it the section depends on.</summary>
public sealed record SectionDependencyProfile(string Section, int FanIn, int FanOut);

/// <summary>The full result of a <c>misplaced</c> analysis.</summary>
public sealed record MisplacedReport(
    IReadOnlyList<MisplacedMethod> Findings,
    IReadOnlyDictionary<string, SectionDependencyProfile> Sections);

/// <summary>
/// <c>misplaced</c> — finds methods whose bodies work on another section's data more than their own.
/// </summary>
/// <remarks>
/// <para>
/// Fowler's feature envy narrowed to one question: <i>is this method in the right assembly?</i> Section
/// identity is the containing assembly (<see cref="AssemblySections"/>), so the fix is a file move across
/// a project boundary, not a rename.
/// </para>
/// <para>
/// Two populations come out of one walk, and keeping them apart is the whole design. <b>Pipes</b> reach
/// exactly one other section and concentrate on it — those have a named destination. <b>Orchestrators</b>
/// reach two or more, so nothing is misplaced, but the shape is also what an accidental junction drawer
/// looks like.
/// </para>
/// <para>
/// Placement is decided by per-section touch counts rather than by per-parameter envy, because an
/// orchestrator is structurally invisible to the classic envy predicate: spreading touches over several
/// sections is the opposite of concentrating them on one. One walk then sees both shapes.
/// </para>
/// <para>
/// Symbols outside the analyzed solution are dropped entirely. Without that gate the BCL becomes sections
/// — <c>System.Runtime</c> reads as a section named <c>Runtime</c> — inflating fan-out enough to turn
/// every pipe into a false orchestrator.
/// </para>
/// </remarks>
public static class MisplacedAnalyzer
{
    /// <summary>
    /// Touches into the target required before a concentration is reported. Below this the shape means
    /// nothing: one call into one other section describes most delegating code in any solution.
    /// </summary>
    public const int MinimumTargetTouches = 3;

    /// <summary>
    /// How much a target must outweigh the method's own section. A method is expected to touch its
    /// neighbours; it is misplaced only when it barely touches home.
    /// </summary>
    public const int DominanceFactor = 2;

    /// <summary>
    /// Distinct other sections at which a method is an orchestrator rather than a pipe. Two, not three:
    /// the claim is that no single section could host it, and that is already true at two.
    /// </summary>
    public const int OrchestratorFanOut = 2;

    /// <summary>Sections that must depend on a section before it can be a foundation rather than a leaf.</summary>
    public const int FoundationMinimumFanIn = 3;

    /// <summary>
    /// How far a section's fan-in must exceed its fan-out before "move this into it" stops being advice.
    /// </summary>
    /// <remarks>
    /// <b>The one tuned number in the command, tuned on a single corpus.</b> On Humans the populations
    /// separate widely: infrastructure at 42:1 and 21:1, domain sections between 12:11 and 35:15. Any
    /// ratio from 4.4 to 21 separates them, and 8 sits in the middle rather than at an edge. A ratio
    /// rather than "fan-out is zero", which found nothing — both infrastructure sections have one
    /// outbound edge. Never load-bearing: every finding prints the target's actual fan-in and fan-out.
    /// </remarks>
    public const int FoundationFanInRatio = 8;

    public static async Task<MisplacedReport> AnalyzeAsync(
        Solution solution,
        IReadOnlyList<ClassifiedType> classified,
        string solutionDirectory,
        int foundationRatio = FoundationFanInRatio,
        CancellationToken ct = default)
    {
        var analyzedAssemblies = new HashSet<string>(
            classified.Select(c => c.Type.ContainingAssembly?.Name).OfType<string>(),
            StringComparer.Ordinal);
        var sectionByAssembly = AssemblySections.Resolve(analyzedAssemblies);

        // Types the config calls a DTO. A project can declare one by rule where the structural test
        // (IsDataCarrier) does not recognise the shape. Keyed by assembly + display name rather than by
        // symbol: `classified` comes from each project's own compilation, so the same type reached from a
        // referencing project is a different symbol instance.
        var configuredDtos = new HashSet<string>(
            classified.Where(c => c.Tags.Contains("dto")).Select(c => SolutionClassifier.TypeKey(c.Type)),
            StringComparer.Ordinal);

        var inheritedContracts = await BuildInheritedContractIndexAsync(solution, analyzedAssemblies, ct);

        // Phase 1 — measure every method. No verdicts yet: the section dependency graph is a property of
        // the whole solution, so nothing that consults it is answerable until every method is counted.
        var measured = new List<MethodTouches>();
        await foreach (var doc in SolutionWalker.ProductionDocumentsAsync(solution, ct))
        {
            foreach (var declaration in doc.Root.DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                ct.ThrowIfCancellationRequested();
                if (declaration.Body is null && declaration.ExpressionBody is null) continue;
                if (doc.Model.GetDeclaredSymbol(declaration, ct) is not IMethodSymbol symbol) continue;

                var ownSection = SectionOf(symbol.ContainingType, sectionByAssembly);
                if (ownSection is null) continue;

                var touches = Measure(
                    symbol, declaration, doc, ownSection, sectionByAssembly, configuredDtos, solutionDirectory, ct);
                if (touches is not null) measured.Add(touches);
            }
        }

        var sections = BuildSectionGraph(measured, sectionByAssembly);

        // Phase 2 — verdicts, now that "is this target a foundation?" is answerable.
        var findings = measured
            .Select(m => Judge(m, sections, inheritedContracts, foundationRatio))
            .OfType<MisplacedMethod>()
            .OrderByDescending(f => f.TargetBehaviorTouches + f.TargetDataTouches)
            .ThenBy(f => f.File, StringComparer.Ordinal)
            .ThenBy(f => f.Line)
            .ToList();

        return new MisplacedReport(findings, sections);
    }

    /// <summary>One method's measured touches, before any verdict is reached about them.</summary>
    private sealed record MethodTouches(
        IMethodSymbol Symbol,
        string Name,
        string File,
        int Line,
        string Section,
        int OwnTouches,
        Dictionary<string, int> Behavior,
        Dictionary<string, int> Data,
        // Behavior touches broken down by the TYPE called, per section. The most-called type in the
        // winning section is the destination — which is what makes a collision claim sayable, since C#
        // only forbids duplicate signatures within one containing type.
        Dictionary<string, Dictionary<INamedTypeSymbol, int>> BehaviorTypes,
        List<string> TouchedSections,
        // The containing type's type parameters this method mentions, or null. They pin it in place.
        string? OuterTypeParameters = null);

    /// <summary>
    /// Fan-in and fan-out per section, counted in distinct sections rather than in touches: the question
    /// is how much of the solution depends on a section, not how chatty any one caller is.
    /// </summary>
    /// <remarks>
    /// Built from every measured method, including the ones no verdict will mention. Restricting it to
    /// reportable findings would let the thresholds decide their own inputs.
    /// </remarks>
    private static Dictionary<string, SectionDependencyProfile> BuildSectionGraph(
        List<MethodTouches> measured, Dictionary<string, string> sectionByAssembly)
    {
        var inbound = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var outbound = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        HashSet<string> Bucket(Dictionary<string, HashSet<string>> map, string key) =>
            map.TryGetValue(key, out var set) ? set : map[key] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var m in measured)
            foreach (var touched in m.TouchedSections)
            {
                Bucket(inbound, touched).Add(m.Section);
                Bucket(outbound, m.Section).Add(touched);
            }

        var names = sectionByAssembly.Values
            .Concat(inbound.Keys)
            .Concat(outbound.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase);

        var result = new Dictionary<string, SectionDependencyProfile>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
            result[name] = new SectionDependencyProfile(
                name,
                inbound.TryGetValue(name, out var i) ? i.Count : 0,
                outbound.TryGetValue(name, out var o) ? o.Count : 0);
        return result;
    }

    /// <summary>Whether a section is shared infrastructure: depended on by many, depending on few.</summary>
    /// <remarks>
    /// A ratio of zero or less turns the category OFF. Without that guard <c>FanOut * 0</c> is zero, so
    /// <c>--foundation-ratio 0</c> would mark everything a foundation and suppress the actionable
    /// findings — the opposite of what asking for no foundation detection means.
    /// </remarks>
    private static bool IsFoundation(SectionDependencyProfile p, int ratio) =>
        ratio > 0 && p.FanIn >= FoundationMinimumFanIn && p.FanIn >= p.FanOut * ratio;

    private static MethodTouches? Measure(
        IMethodSymbol symbol,
        MethodDeclarationSyntax declaration,
        SolutionDocument doc,
        string ownSection,
        Dictionary<string, string> sectionByAssembly,
        HashSet<string> configuredDtos,
        string solutionDirectory,
        CancellationToken ct)
    {
        int ownTouches = 0;
        var behavior = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var data = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var behaviorTypes =
            new Dictionary<string, Dictionary<INamedTypeSymbol, int>>(StringComparer.OrdinalIgnoreCase);

        SyntaxNode body = declaration.Body ?? (SyntaxNode)declaration.ExpressionBody!;
        foreach (var node in body.DescendantNodesAndSelf())
        {
            ct.ThrowIfCancellationRequested();

            // Names, plus the expressions that carry a symbol somewhere OTHER than a name: object
            // creation carries its constructor, element access and element binding (`x?[0]`) carry the
            // indexer, and an operator expression carries the user-defined operator. Member bindings are
            // deliberately absent — their own `.Name` is an identifier this walk already visits, so
            // listing both scored every null-conditional call twice.
            if (node is not (IdentifierNameSyntax or GenericNameSyntax
                or BaseObjectCreationExpressionSyntax
                or ElementAccessExpressionSyntax or ElementBindingExpressionSyntax
                or BinaryExpressionSyntax or PrefixUnaryExpressionSyntax or PostfixUnaryExpressionSyntax
                or AssignmentExpressionSyntax or CastExpressionSyntax)) continue;

            // `nameof(Foo.Bar)` binds to Bar but executes nothing.
            if (IsInsideNameOf(node)) continue;

            // A type name inside `new T(...)` would otherwise be counted twice, as type and constructor.
            if (node is (IdentifierNameSyntax or GenericNameSyntax)
                && node.Parent is ObjectCreationExpressionSyntax creation && creation.Type == node) continue;

            var touched = doc.Model.GetSymbolInfo(node, ct).Symbol;
            if (touched is null) continue;
            if (touched is not (IMethodSymbol or IPropertySymbol or IFieldSymbol or IEventSymbol)) continue;

            // Operator-shaped nodes are a touch only when the operator is USER-DEFINED. `int + int`
            // resolves to a builtin, and `_dep!` or a reference cast is a wrapper, not a call.
            if (node is BinaryExpressionSyntax or PrefixUnaryExpressionSyntax or PostfixUnaryExpressionSyntax
                    or AssignmentExpressionSyntax or CastExpressionSyntax
                && touched is not IMethodSymbol
                    { MethodKind: MethodKind.UserDefinedOperator or MethodKind.Conversion }) continue;

            // A local function's body is walked here too, but the local function symbol itself is not
            // another type's member and carries no section.
            var owner = touched.ContainingType;
            if (owner is null) continue;

            var section = SectionOf(owner, sectionByAssembly);
            if (section is null) continue;

            if (string.Equals(section, ownSection, StringComparison.OrdinalIgnoreCase))
            {
                // A field or property that only exists to reach another section is a conduit, not state
                // this method works on. Counted as own state it made delegation invisible: in
                // `_dep.Method()` the receiver scores 1 at home and the call 1 away, so a method that
                // does nothing but delegate ties 1:1. Dropped from both sides rather than moved to the
                // target, which would double the target count and shift every threshold with it.
                if (touched is IFieldSymbol or IPropertySymbol
                    && IsConduit(node, doc, ownSection, sectionByAssembly, ct)) continue;

                ownTouches++;
                continue;
            }

            if (IsData(owner.OriginalDefinition, AccessedThrough(node, doc, ct), touched, configuredDtos))
            {
                data[section] = data.TryGetValue(section, out var d) ? d + 1 : 1;
                continue;
            }

            behavior[section] = behavior.TryGetValue(section, out var n) ? n + 1 : 1;

            var byType = behaviorTypes.TryGetValue(section, out var existing)
                ? existing
                : behaviorTypes[section] = new Dictionary<INamedTypeSymbol, int>(SymbolEqualityComparer.Default);
            var definition = owner.OriginalDefinition;
            byType[definition] = byType.TryGetValue(definition, out var t) ? t + 1 : 1;
        }

        var touchedSections = behavior.Keys.Concat(data.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();
        if (touchedSections.Count == 0) return null;

        var lineSpan = declaration.Identifier.GetLocation().GetLineSpan();
        return new MethodTouches(
            symbol,
            $"{symbol.ContainingType.Name}.{symbol.Name}",
            LocationHelper.NormalizePath(lineSpan.Path, solutionDirectory),
            lineSpan.StartLinePosition.Line + 1,
            ownSection,
            ownTouches,
            behavior,
            data,
            behaviorTypes,
            touchedSections,
            AnyEnclosingTypeIsGeneric(symbol.ContainingType)
                ? OuterTypeParameters(declaration, doc, ct)
                : null);
    }

    /// <summary>Whether this type or any type enclosing it declares type parameters.</summary>
    private static bool AnyEnclosingTypeIsGeneric(INamedTypeSymbol type)
    {
        for (INamedTypeSymbol? t = type; t is not null; t = t.ContainingType)
            if (t.TypeParameters.Length > 0) return true;
        return false;
    }

    /// <summary>The containing type's type parameters this method mentions, or null if it mentions none.</summary>
    /// <remarks>
    /// Such a parameter is scoped to the containing type, so no destination can declare the method as
    /// written. A method's OWN type parameters travel with it, hence
    /// <see cref="TypeParameterKind.Type"/> only. Reads the whole declaration, so it sees the signature
    /// and every explicit mention in the body, but not a parameter reached only by inference.
    /// </remarks>
    private static string? OuterTypeParameters(
        MethodDeclarationSyntax declaration, SolutionDocument doc, CancellationToken ct)
    {
        SortedSet<string>? used = null;
        foreach (var node in declaration.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>())
        {
            ct.ThrowIfCancellationRequested();
            if (doc.Model.GetSymbolInfo(node, ct).Symbol
                is not ITypeParameterSymbol { TypeParameterKind: TypeParameterKind.Type } parameter) continue;
            (used ??= new SortedSet<string>(StringComparer.Ordinal)).Add(parameter.Name);
        }
        return used is null ? null : string.Join(", ", used);
    }

    private static MisplacedMethod? Judge(
        MethodTouches m,
        Dictionary<string, SectionDependencyProfile> sections,
        Dictionary<string, string> inheritedContracts,
        int foundationRatio)
    {
        if (m.TouchedSections.Count >= OrchestratorFanOut)
        {
            // No dominance test: an orchestrator is defined by its reach, and requiring it to also
            // out-touch home would only report the ones holding no state. The per-section split is
            // evidence, not decoration — at a fan-out of two, even-versus-lopsided is the whole reading.
            int reached = m.Behavior.Values.Sum() + m.Data.Values.Sum();
            var parts = m.TouchedSections.Select(s =>
                $"{s}:{(m.Behavior.TryGetValue(s, out var b) ? b : 0) + (m.Data.TryGetValue(s, out var d) ? d : 0)}");
            return Finding(m, null, 0, 0, MisplacedVerdict.Orchestrator,
                $"reaches {m.TouchedSections.Count} other sections in {Touches(reached)} " +
                $"({string.Join(", ", parts)}); {Touches(m.OwnTouches)} on {m.Section}");
        }

        var target = m.TouchedSections[0];
        int behaviorTouches = m.Behavior.TryGetValue(target, out var bt) ? bt : 0;
        int dataTouches = m.Data.TryGetValue(target, out var dt) ? dt : 0;
        int total = behaviorTouches + dataTouches;

        if (total < MinimumTargetTouches) return null;
        if (total <= m.OwnTouches * DominanceFactor) return null;

        var profile = sections.TryGetValue(target, out var p) ? p : new SectionDependencyProfile(target, 0, 0);
        var reach = $"{profile.FanIn} section(s) depend on {target}, {target} depends on {profile.FanOut}";

        if (IsFoundation(profile, foundationRatio))
            return Finding(m, target, behaviorTouches, dataTouches, MisplacedVerdict.FoundationTarget,
                $"{Touches(total)} into {target}, {Touches(m.OwnTouches)} on {m.Section} — but {reach}, " +
                $"so {target} is shared infrastructure and no move is proposed");

        // Reads of another section's DTO properties are how mapping code looks from here, and a mapper
        // belongs to whoever needs the mapped shape. Only calls into behavior argue for a move.
        if (behaviorTouches < MinimumTargetTouches)
            return Finding(m, target, behaviorTouches, dataTouches, MisplacedVerdict.Mapper,
                $"{dataTouches} read(s) of {target} data carriers, {behaviorTouches} call(s) into {target} behavior, " +
                $"{Touches(m.OwnTouches)} on {m.Section} — reads {target}'s data rather than using it");

        var evidence =
            $"{behaviorTouches} call(s) into {target}" +
            (dataTouches > 0 ? $" plus {dataTouches} read(s) of its data" : "") +
            $", {Touches(m.OwnTouches)} on {m.Section}; {reach}";

        if (Contract(m.Symbol, inheritedContracts) is { } blockedBy)
            return Finding(m, target, behaviorTouches, dataTouches, MisplacedVerdict.Blocked, evidence,
                blockedBy: blockedBy);

        // The other way a method is pinned: a type parameter the CONTAINING type declares. Relocation is
        // then a generic redesign rather than a move.
        if (m.OuterTypeParameters is { } pinned)
            return Finding(m, target, behaviorTouches, dataTouches, MisplacedVerdict.Blocked, evidence,
                blockedBy: $"uses type parameter {pinned} of {m.Symbol.ContainingType.OriginalDefinition.ToDisplayString()}, " +
                    "which is scoped to that type and does not exist at the destination");

        var destination = DestinationType(m, target);
        var collision = destination is null ? null : Collision(m.Symbol, destination);

        return Finding(m, target, behaviorTouches, dataTouches,
            collision is null ? MisplacedVerdict.Move : MisplacedVerdict.MoveWouldDuplicate,
            evidence, duplicateOf: collision, destinationType: QualifiedName(destination));
    }

    /// <summary>
    /// What the destination already declares under this method's name: whether the signature matches,
    /// and if not, how it differs.
    /// </summary>
    /// <remarks>
    /// Deliberately stops short of claiming the two methods DO the same thing — that needs the bodies
    /// compared. An identical signature is still decisive: the destination will not compile with both.
    /// A namesake with different parameters is a weaker signal, reported as such.
    /// </remarks>
    private static string? Collision(IMethodSymbol moving, INamedTypeSymbol destination)
    {
        var candidates = destination.GetMembers(moving.Name)
            .OfType<IMethodSymbol>()
            .Where(m => m.MethodKind == MethodKind.Ordinary)
            .ToList();
        if (candidates.Count == 0) return null;

        // Exact signature first: it is decisive, so it must not hide behind an earlier near-miss.
        foreach (var candidate in candidates)
            if (SameSignature(moving, candidate))
                return $"{Describe(candidate)} — same signature, so {destination.Name} cannot declare both";

        var namesake = candidates[0];
        var difference = moving.Parameters.Length == namesake.Parameters.Length
            ? "same arity, different parameter types"
            : $"{namesake.Parameters.Length} parameter(s) against this method's {moving.Parameters.Length}";
        return $"{Describe(namesake)} — {difference}, so reconcile the two rather than copying either";
    }

    /// <summary>The type in <paramref name="target"/> this method calls most — the natural host if it moves.</summary>
    /// <remarks>
    /// Only called types count, never data carriers: a method that reads another section's DTOs is judged
    /// a mapper before this is reached. The answer may be an INTERFACE, which cannot host a body — but
    /// the collision question is still well posed against it, and naming it tells the reader which
    /// contract the move would run into.
    /// </remarks>
    private static INamedTypeSymbol? DestinationType(MethodTouches m, string target)
    {
        if (!m.BehaviorTypes.TryGetValue(target, out var byType) || byType.Count == 0) return null;

        INamedTypeSymbol? best = null;
        int bestCount = 0;
        foreach (var (type, count) in byType)
        {
            // Ties break on name: iteration order over a symbol-keyed dictionary is not something to
            // build output on.
            if (count > bestCount ||
                (count == bestCount && best is not null &&
                 string.CompareOrdinal(type.Name, best.Name) < 0))
            {
                (best, bestCount) = (type, count);
            }
        }
        return best;
    }

    /// <summary>Whether a parameter is passed by reference, in the sense that matters to overload resolution.</summary>
    /// <remarks>
    /// C# refuses two declarations differing only in <c>ref</c> vs <c>out</c> vs <c>in</c> (CS0663), so
    /// all of them collapse to one answer here.
    /// </remarks>
    private static bool IsByReference(RefKind kind) => kind != RefKind.None;

    /// <summary>
    /// Whether this touch is a data read rather than a behavior call: the touched member carries data on
    /// a configured DTO, or the type's shape says it carries data and nothing else.
    /// </summary>
    /// <remarks>
    /// The union is the one <see cref="SectionShapeAnalyzer"/> and <see cref="CanonicalReadDtoSet"/>
    /// already take. The <paramref name="touched"/> member matters on the <b>configured</b> side only: a
    /// config rule labels a type's role and says nothing about its members, so calling a method on a
    /// configured DTO is still a behavior call. The structural test needs no such guard — it rejects any
    /// type exposing behavior at all.
    /// </remarks>
    private static bool IsData(
        INamedTypeSymbol type, INamedTypeSymbol? through, ISymbol touched, HashSet<string> configuredDtos) =>
        // An enum has no behavior to call; the structural test misses them only because it gates on
        // class-or-struct. Counted as calls, enum members let an enum WIN the destination contest —
        // proposing a method be moved onto a type that cannot declare one.
        type.TypeKind == TypeKind.Enum
        || CanonicalReadDtoSet.IsDataCarrier(type)
        || (touched is IPropertySymbol or IFieldSymbol
            && (configuredDtos.Contains(SolutionClassifier.TypeKey(type))
                || (through is not null && configuredDtos.Contains(SolutionClassifier.TypeKey(through)))));

    /// <summary>
    /// The type this member was reached <i>through</i> — the static type of the receiver in
    /// <c>receiver.Member</c> — or null when the access has no receiver to read.
    /// </summary>
    /// <remarks>
    /// A member's containing type is where it is <b>declared</b>, not what the caller holds. A configured
    /// DTO inheriting <c>Id</c> from a base the config does not name reports that base, so <c>result.Id</c>
    /// read as a behavior call and could turn a mapper into a move. Used only for the configured-DTO
    /// test; the section a touch is attributed to still comes from the declaring type.
    /// </remarks>
    private static INamedTypeSymbol? AccessedThrough(SyntaxNode node, SolutionDocument doc, CancellationToken ct)
    {
        var receiver = node switch
        {
            // An indexer read IS the access, so it carries its own receiver rather than hanging off a
            // parent: `row[0]` keeps it on the element access, `row?[0]` on the conditional access.
            ElementAccessExpressionSyntax element => element.Expression,
            ElementBindingExpressionSyntax binding =>
                binding.Ancestors().OfType<ConditionalAccessExpressionSyntax>().FirstOrDefault()?.Expression,

            // `receiver.Member`, and `receiver?.Member` where the name sits under a member binding and
            // the receiver hangs off the enclosing conditional access.
            _ => node.Parent switch
            {
                MemberAccessExpressionSyntax access when access.Name == node => access.Expression,
                MemberBindingExpressionSyntax binding when binding.Name == node =>
                    binding.Ancestors().OfType<ConditionalAccessExpressionSyntax>().FirstOrDefault()?.Expression,
                _ => null
            }
        };
        // `row is { Id: > 0 }` binds `Id` with no receiver expression anywhere near it: the receiver is
        // the pattern's INPUT.
        if (receiver is null)
            return PatternInputType(node, doc, ct)?.OriginalDefinition as INamedTypeSymbol;

        return doc.Model.GetTypeInfo(receiver, ct).Type?.OriginalDefinition as INamedTypeSymbol;
    }

    /// <summary>The type a property-pattern subpattern is matched against, or null if this node is not one.</summary>
    /// <remarks>
    /// The input is stated in one of four places and the nearest wins: the pattern's own type
    /// (<c>is Row { … }</c>), the enclosing subpattern's property (<c>{ Row: { … } }</c>), or the
    /// expression the whole pattern matches — an <c>is</c> operand, or a switch expression's or
    /// statement's governing expression.
    /// </remarks>
    private static ITypeSymbol? PatternInputType(SyntaxNode node, SolutionDocument doc, CancellationToken ct)
    {
        if (node.Parent is not (NameColonSyntax or ExpressionColonSyntax)) return null;
        if (node.Ancestors().OfType<RecursivePatternSyntax>().FirstOrDefault() is not { } pattern) return null;
        if (pattern.Type is { } declared) return doc.Model.GetTypeInfo(declared, ct).Type;

        foreach (var ancestor in pattern.Ancestors())
            switch (ancestor)
            {
                case SubpatternSyntax { NameColon.Name: { } outerName }:
                    return (doc.Model.GetSymbolInfo(outerName, ct).Symbol as IPropertySymbol)?.Type;
                case RecursivePatternSyntax { Type: { } outerType }:
                    return doc.Model.GetTypeInfo(outerType, ct).Type;
                case IsPatternExpressionSyntax isPattern:
                    return doc.Model.GetTypeInfo(isPattern.Expression, ct).Type;
                case SwitchExpressionSyntax switchExpression:
                    return doc.Model.GetTypeInfo(switchExpression.GoverningExpression, ct).Type;
                case SwitchStatementSyntax switchStatement:
                    return doc.Model.GetTypeInfo(switchStatement.Expression, ct).Type;
            }
        return null;
    }

    /// <summary>Whether two methods collide as C# declarations.</summary>
    /// <remarks>
    /// The RETURN TYPE is deliberately not compared: C# does not permit overloading on return type, so
    /// two methods alike in name and parameters are a collision, not a pair of overloads.
    /// </remarks>
    private static bool SameSignature(IMethodSymbol a, IMethodSymbol b)
    {
        if (a.Parameters.Length != b.Parameters.Length) return false;
        if (a.TypeParameters.Length != b.TypeParameters.Length) return false;

        for (int i = 0; i < a.Parameters.Length; i++)
        {
            var (x, y) = (a.Parameters[i], b.Parameters[i]);
            if (IsByReference(x.RefKind) != IsByReference(y.RefKind)) return false;
            if (!SameParameterType(x.Type, y.Type)) return false;
        }
        return true;
    }

    /// <summary>Whether two parameter types are the same type <i>for signature purposes</i>.</summary>
    /// <remarks>
    /// A method's own type parameters are distinct symbols per method, so <c>Map&lt;T&gt;(T)</c> and
    /// <c>Map&lt;U&gt;(U)</c> compare unequal under <see cref="SymbolEqualityComparer.Default"/> while C#
    /// reads them as one signature — matched by <see cref="ITypeParameterSymbol.Ordinal"/> instead. Only
    /// <see cref="TypeParameterKind.Method"/> is matched that way; a type-level parameter belongs to a
    /// class that is not moving, so symbol equality is the answer that keeps the claim true. The walk is
    /// structural because a type parameter can be nested. Nullable annotation is not compared: it is not
    /// part of a C# signature.
    /// </remarks>
    private static bool SameParameterType(ITypeSymbol a, ITypeSymbol b)
    {
        if (a is ITypeParameterSymbol { TypeParameterKind: TypeParameterKind.Method } pa)
            return b is ITypeParameterSymbol { TypeParameterKind: TypeParameterKind.Method } pb
                && pa.Ordinal == pb.Ordinal;

        if (a is IArrayTypeSymbol arrayA)
            return b is IArrayTypeSymbol arrayB
                && arrayA.Rank == arrayB.Rank
                && SameParameterType(arrayA.ElementType, arrayB.ElementType);

        if (a is IPointerTypeSymbol pointerA)
            return b is IPointerTypeSymbol pointerB
                && SameParameterType(pointerA.PointedAtType, pointerB.PointedAtType);

        if (a is INamedTypeSymbol { IsGenericType: true } namedA
            && b is INamedTypeSymbol { IsGenericType: true } namedB)
        {
            if (!SymbolEqualityComparer.Default.Equals(namedA.OriginalDefinition, namedB.OriginalDefinition))
                return false;
            if (namedA.TypeArguments.Length != namedB.TypeArguments.Length) return false;
            for (int i = 0; i < namedA.TypeArguments.Length; i++)
                if (!SameParameterType(namedA.TypeArguments[i], namedB.TypeArguments[i])) return false;
            return true;
        }

        return SymbolEqualityComparer.Default.Equals(a, b);
    }

    private static string Describe(IMethodSymbol method) =>
        $"{method.ContainingType.Name}.{method.Name}({string.Join(", ", method.Parameters.Select(p => p.Type.Name))})";

    private static MisplacedMethod Finding(
        MethodTouches m, string? target, int behaviorTouches, int dataTouches,
        MisplacedVerdict verdict, string evidence, string? duplicateOf = null, string? blockedBy = null,
        string? destinationType = null) =>
        new(m.Name, m.File, m.Line, m.Section, target, m.OwnTouches, behaviorTouches, dataTouches,
            m.TouchedSections, verdict, evidence, duplicateOf, blockedBy, destinationType);

    /// <summary>
    /// Whether this own-section field or property is being used purely as the receiver through which
    /// another section is reached.
    /// </summary>
    /// <remarks>
    /// The walk climbs out of the member-access chain to find the access this node is the <i>receiver</i>
    /// of, so <c>this._dep.Method()</c> reads the same as <c>_dep.Method()</c>. When the node is the
    /// member <i>being</i> accessed rather than the receiver, it is a real touch.
    /// </remarks>
    private static bool IsConduit(
        SyntaxNode node, SolutionDocument doc, string ownSection,
        Dictionary<string, string> sectionByAssembly, CancellationToken ct)
    {
        var current = node;
        while (true)
        {
            SyntaxNode? reached = current.Parent switch
            {
                // On the name side of a member access, not the receiver side: climb, so a qualified
                // receiver such as `this._dep` still resolves to the access it is the receiver of.
                MemberAccessExpressionSyntax parent when parent.Expression != current => null,
                MemberAccessExpressionSyntax parent => parent.Name,

                // `?.` puts the receiver under a conditional access and the reached member on the other
                // side of it — a member binding for `?.Method`, an ELEMENT binding for `?[0]`.
                ConditionalAccessExpressionSyntax conditional when conditional.Expression == current =>
                    conditional.WhenNotNull
                        .DescendantNodesAndSelf()
                        .FirstOrDefault(n => n is MemberBindingExpressionSyntax or ElementBindingExpressionSyntax)
                        switch
                        {
                            MemberBindingExpressionSyntax member => member.Name,
                            // The element binding IS the access, so it resolves to the indexer directly.
                            ElementBindingExpressionSyntax element => element,
                            _ => null
                        },

                // `_table[0]` reaches another section through an indexer, and the receiver is as much a
                // conduit as in `_dep.Method()`. The element access IS the access, so it is what gets
                // resolved rather than a name.
                ElementAccessExpressionSyntax element when element.Expression == current => element,

                _ => null
            };

            if (reached is not null)
            {
                var accessed = doc.Model.GetSymbolInfo(reached, ct).Symbol;
                if (accessed?.ContainingType is not { } owner) return false;
                var section = SectionOf(owner, sectionByAssembly);
                return section is not null
                    && !string.Equals(section, ownSection, StringComparison.OrdinalIgnoreCase);
            }

            // Keep climbing only while there is a wrapper that leaves this node the receiver — a cast,
            // `!`, parentheses, or an await all reach the same thing `_dep.Method()` does.
            bool transparent = current.Parent switch
            {
                MemberAccessExpressionSyntax or ParenthesizedExpressionSyntax => true,
                PostfixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.SuppressNullableWarningExpression } => true,
                CastExpressionSyntax cast => cast.Expression == current,
                AwaitExpressionSyntax await => await.Expression == current,
                // `_dep as IForeign` — only the left side is the value; the right side is a type name.
                BinaryExpressionSyntax { RawKind: (int)SyntaxKind.AsExpression } asExpression =>
                    asExpression.Left == current,
                _ => false
            };
            if (transparent)
            {
                current = current.Parent!;
                continue;
            }
            return false;
        }
    }

    /// <summary>
    /// The contract that pins a method to its type. Such a method cannot move alone: the declaration
    /// would have to move too, which is a larger change than relocating a file.
    /// </summary>
    private static string? Contract(IMethodSymbol symbol, Dictionary<string, string> inheritedContracts)
    {
        // A default interface method is not bound BY a contract, it IS one — and neither branch below
        // catches it, since `AllInterfaces` excludes the interface a member is declared on.
        if (symbol.ContainingType.TypeKind == TypeKind.Interface)
            return $"declared on the interface {symbol.ContainingType.Name}";

        // The implementation half of a partial method: C# requires both halves in one containing type,
        // so the body cannot travel without the declaration.
        if (symbol.PartialDefinitionPart is not null)
            return $"the implementation part of partial {symbol.ContainingType.Name}.{symbol.Name}";

        if (symbol.IsOverride && symbol.OverriddenMethod is { } overridden)
            return $"overrides {overridden.ContainingType.Name}.{overridden.Name}";

        if (symbol.ExplicitInterfaceImplementations.Length > 0)
        {
            var explicitly = symbol.ExplicitInterfaceImplementations[0];
            return $"implements {explicitly.ContainingType.Name}.{explicitly.Name}";
        }

        foreach (var iface in symbol.ContainingType.AllInterfaces)
            foreach (var member in iface.GetMembers(symbol.Name))
                if (member is IMethodSymbol candidate
                    && SymbolEqualityComparer.Default.Equals(
                        symbol.ContainingType.FindImplementationForInterfaceMember(candidate), symbol))
                    return $"implements {iface.Name}.{candidate.Name}";

        // Last, because it is the only branch needing an index: the contract may belong to a type
        // further down the hierarchy rather than to this one.
        return inheritedContracts.TryGetValue(MethodKey(symbol.OriginalDefinition), out var inherited)
            ? inherited
            : null;
    }

    /// <summary>Namespace-qualified name of the destination type, or null when none was chosen.</summary>
    /// <remarks>
    /// Qualified because a section can hold two types of one name in different namespaces and nothing
    /// else in the output says which was meant. <see cref="ISymbol.ToDisplayString()"/> rather than
    /// namespace-plus-name assembled by hand, which drops containing types and generic arity.
    /// </remarks>
    private static string? QualifiedName(INamedTypeSymbol? type) => type?.ToDisplayString();

    /// <summary><c>1 touch</c> / <c>4 touches</c>. The output is read by people and by models.</summary>
    private static string Touches(int n) => n == 1 ? "1 touch" : $"{n} touches";

    private static string? SectionOf(INamedTypeSymbol type, Dictionary<string, string> sectionByAssembly) =>
        type.ContainingAssembly?.Name is { } assembly && sectionByAssembly.TryGetValue(assembly, out var section)
            ? section
            : null;

    /// <summary>
    /// Methods pinned by a relationship only visible from ANOTHER type: an interface member they
    /// implement for a derived type, or a base declaration some derived type overrides.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>class Derived : Base, IFoo</c> can satisfy <c>IFoo.M</c> with an inherited <c>Base.M</c>. Asked
    /// from <c>Base</c> there is no interface to find, so the method reads as movable while moving it
    /// leaves <c>Derived</c> without an implementation. Same for the reverse: nothing on a base
    /// declaration says it IS overridden. Both have to be indexed from every type.
    /// </para>
    /// <para>
    /// Keyed by string rather than by symbol: types come from each project's own compilation, so the
    /// symbol for the same method in a referencing project is an instance
    /// <see cref="SymbolEqualityComparer"/> would miss. Walks <b>every</b> type in each compilation
    /// rather than the classified list, which drops effectively private types — a private
    /// <c>Derived : Base, IFoo</c> constrains a public <c>Base.M</c> exactly as a public one does.
    /// </para>
    /// </remarks>
    private static async Task<Dictionary<string, string>> BuildInheritedContractIndexAsync(
        Solution solution, HashSet<string> analyzedAssemblies, CancellationToken ct)
    {
        var index = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var project in solution.Projects)
        {
            ct.ThrowIfCancellationRequested();
            if (project.AssemblyName is not { } assembly || !analyzedAssemblies.Contains(assembly)) continue;
            if (await project.GetCompilationAsync(ct) is not { } compilation) continue;

            foreach (var type in SolutionClassifier.EnumerateAllTypes(compilation.GlobalNamespace))
            {
                ct.ThrowIfCancellationRequested();
                if (type.TypeKind is not (TypeKind.Class or TypeKind.Struct)) continue;

                // A compilation's global namespace spans its references too, so without this every
                // project re-walked its whole dependency closure and the pass scaled with repeated
                // references rather than with distinct types. Each assembly still visits its own.
                if (!SymbolEqualityComparer.Default.Equals(type.ContainingAssembly, compilation.Assembly))
                    continue;

                foreach (var iface in type.AllInterfaces)
                    foreach (var member in iface.GetMembers())
                    {
                        if (member is not IMethodSymbol candidate) continue;
                        if (type.FindImplementationForInterfaceMember(candidate) is not IMethodSymbol impl) continue;

                        // Declared on this very type is the case Contract answers without an index.
                        if (SymbolEqualityComparer.Default.Equals(impl.ContainingType, type)) continue;

                        // The ORIGINAL definition: a contract supplied through a constructed base
                        // resolves to `Base<int>.M(int)` while the method measured from syntax is
                        // `Base<T>.M(T)`, and keyed as substituted the two never meet.
                        index.TryAdd(
                            MethodKey(impl.OriginalDefinition),
                            $"implements {iface.Name}.{candidate.Name} for {type.Name}");
                    }

                // Only the immediate base needs recording: in a three-deep chain the middle declaration
                // is itself an override and pins its own base when its type is visited.
                foreach (var member in type.GetMembers())
                {
                    if (member is not IMethodSymbol { IsOverride: true } over) continue;
                    if (over.OverriddenMethod is not { } overridden) continue;
                    index.TryAdd(
                        MethodKey(overridden.OriginalDefinition),
                        $"overridden by {type.Name}.{over.Name}");
                }
            }
        }

        return index;
    }

    /// <summary>
    /// Identifies a method across compilations: declaring type, name, parameter types and arity.
    /// </summary>
    /// <remarks>
    /// The ref kind belongs here even though collision deliberately IGNORES it. Opposite questions:
    /// collision asks "could one type declare both?", where ref and out are the same answer, while this
    /// asks "which method is this?", where <c>M(int)</c> and <c>M(ref int)</c> are two methods.
    /// </remarks>
    private static string MethodKey(IMethodSymbol method) =>
        $"{SolutionClassifier.TypeKey(method.ContainingType)}|{method.Name}|" +
        string.Join(",", method.Parameters.Select(p => $"{p.RefKind}:{p.Type.ToDisplayString()}")) +
        $"|{method.TypeParameters.Length}";

    /// <summary>
    /// Whether the node sits inside a <c>nameof(...)</c> argument. <c>nameof</c> is not a keyword, so the
    /// test is on the invoked identifier.
    /// </summary>
    private static bool IsInsideNameOf(SyntaxNode node)
    {
        for (var current = node.Parent; current is not null; current = current.Parent)
        {
            if (current is InvocationExpressionSyntax
                { Expression: IdentifierNameSyntax { Identifier.ValueText: "nameof" } }) return true;
            if (current is MemberDeclarationSyntax or StatementSyntax) return false;
        }
        return false;
    }
}
