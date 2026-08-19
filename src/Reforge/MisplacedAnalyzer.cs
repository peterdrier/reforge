using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.CSharp;

namespace Reforge;

/// <summary>What the analyzer concluded about one method's placement.</summary>
public enum MisplacedVerdict
{
    /// <summary>
    /// The method's body is almost entirely another section's behavior, and that section has nothing
    /// by the same name. Move it there.
    /// </summary>
    Move,

    /// <summary>
    /// Same shape as <see cref="Move"/>, except the target section already declares a method with
    /// this name. Moving it verbatim risks a duplicate — read both before moving either.
    /// </summary>
    MoveWouldDuplicate,

    /// <summary>
    /// Concentrated on a section the whole solution depends on and which depends on none of it — a
    /// foundation. "Move it into the foundation" is not the advice; a thin wrapper over shared
    /// infrastructure is what this shape usually is.
    /// </summary>
    FoundationTarget,

    /// <summary>
    /// The method reaches two or more other sections. That is not misplacement — no single section
    /// could host it — but it IS the population worth reading, because a genuine orchestrator and an
    /// accidental junction drawer look identical from here.
    /// </summary>
    Orchestrator,

    /// <summary>
    /// Concentrated on another section, but the touches are reads of that section's data carriers
    /// rather than calls into its behavior. Usually a mapper, which belongs on the consuming side.
    /// </summary>
    Mapper,

    /// <summary>
    /// Concentrated on another section but the method cannot move on its own: it implements an
    /// interface member or overrides a base member, so the contract would have to move with it.
    /// </summary>
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
    /// The concrete type in <see cref="TargetSection"/> this method leans on hardest — the natural host
    /// if it moves. Null when no destination was chosen (orchestrators, or nothing callable in the
    /// target). Reported because the section alone is not where a method goes, and because the choice
    /// should be auditable rather than visible only when a namesake happens to collide with it.
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
/// This is Fowler's feature envy narrowed to one question: <i>is this method in the right
/// assembly?</i> A method that barely touches its own section's state and repeatedly calls into
/// another section's is doing that section's work from the outside. Section identity is the
/// containing assembly (<see cref="AssemblySections"/>), so the finding is structural rather than
/// stylistic — the fix is a file move across a project boundary, not a rename.
/// </para>
/// <para>
/// Two populations come out of one walk, and keeping them apart is the whole design:
/// </para>
/// <list type="bullet">
///   <item><b>Pipes</b> reach exactly one other section and concentrate on it. These are the
///         actionable ones: there is a named destination.</item>
///   <item><b>Orchestrators</b> reach two or more. Nothing is misplaced — the method exists
///         precisely to join sections — so no destination is proposed. They are reported because
///         the shape is also what an accidental junction drawer looks like.</item>
/// </list>
/// <para>
/// An orchestrator is <b>structurally invisible</b> to the classic envy predicate (touches
/// concentrated on one parameter, exceeding the method's own state), because spreading touches over
/// several sections is the opposite of concentrating them. That is why placement here is decided by
/// per-section touch counts rather than by per-parameter envy: one walk then sees both shapes.
/// </para>
/// <para>
/// Every touch is attributed to the section of the symbol's containing assembly, and symbols outside
/// the analyzed solution are dropped entirely. Without that gate the BCL becomes sections —
/// <c>System.Runtime</c> reads as a section named <c>Runtime</c> — which inflates fan-out enough to
/// turn every pipe into a false orchestrator.
/// </para>
/// </remarks>
public static class MisplacedAnalyzer
{
    /// <summary>
    /// Touches into the target section required before a concentration is reported at all. Below
    /// this a method is too small for its shape to mean anything: one call into one other section
    /// describes most delegating code in any solution.
    /// </summary>
    public const int MinimumTargetTouches = 3;

    /// <summary>
    /// How much a target section must outweigh the method's own section. A method is expected to
    /// touch its neighbours; it is misplaced only when it barely touches home.
    /// </summary>
    public const int DominanceFactor = 2;

    /// <summary>
    /// Distinct other sections at which a method is called an orchestrator, not a pipe.
    /// </summary>
    /// <remarks>
    /// <b>Two</b>, not three. The orchestrator argument is that no single section could host the
    /// method, and that is already true at two — neither of the two could take it without leaving the
    /// other reached from the wrong side. Three was arbitrary, and the two-section population it left
    /// behind was reported under a separate verdict that made exactly the same claim in weaker words.
    /// </remarks>
    public const int OrchestratorFanOut = 2;

    /// <summary>
    /// Sections that must depend on a section before it can be called a foundation at all, rather than
    /// a leaf nobody uses.
    /// </summary>
    public const int FoundationMinimumFanIn = 3;

    /// <summary>
    /// How far a section's fan-in must exceed its fan-out before "move this into it" stops being
    /// advice. Infrastructure is depended upon by the solution and depends on almost none of it;
    /// a domain section is roughly balanced.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the one tuned number in the command, and it is tuned on a single corpus.</b> On
    /// Humans the two populations separate with a wide margin: the infrastructure sections are 42:1
    /// (<c>Base</c>) and 21:1 (<c>Gdpr</c>), while the domain sections are 35:15 (<c>Users</c>),
    /// 24:13 (<c>Teams</c>), 16:13 (<c>Shifts</c>), 12:11 (<c>Camps</c>). Any ratio between 4.4 and
    /// 21 separates them; 8 sits in the middle of that window rather than at either edge. With a
    /// second corpus this would be measured instead of chosen, and it is adjustable per run.
    /// </para>
    /// <para>
    /// A ratio rather than "fan-out is zero", which was the first attempt: <c>Base</c> has exactly
    /// one outbound edge and <c>Gdpr</c> one, so a zero test found nothing. That first reading came
    /// from the reportable findings rather than from every measured method — a threshold measured on
    /// a population the thresholds had already filtered.
    /// </para>
    /// <para>
    /// The categorization is never load-bearing: every finding prints the target's actual fan-in and
    /// fan-out, so a reader can disagree with this number from the output alone.
    /// </para>
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

        // Types the active config calls a DTO. The structural test below (IsDataCarrier) recognises the
        // shape of a data carrier, but a project can declare one by rule — and a configured DTO whose
        // shape the heuristic does not recognise had its property reads counted as behavior, which is
        // how a method reading three of them was reported as a `move` rather than the `mapper` it is.
        // Keyed by assembly + display name rather than by symbol: `classified` is collected from each
        // project's own compilation, so the same type reached from a referencing project is a different
        // symbol instance and SymbolEqualityComparer would miss it.
        var configuredDtos = new HashSet<string>(
            classified.Where(c => c.Tags.Contains("dto")).Select(c => SolutionClassifier.TypeKey(c.Type)),
            StringComparer.Ordinal);

        // Contracts a method satisfies for some OTHER type. `Derived : Base, IFoo` can be served by an
        // inherited `Base.M`, and Base.AllInterfaces does not mention IFoo — so a dominant Base.M read as
        // freely movable, when relocating it leaves Derived without its IFoo.M and the solution will not
        // compile. Built once here because the question cannot be answered from the declaring type alone.
        var inheritedContracts = await BuildInheritedContractIndexAsync(solution, analyzedAssemblies, ct);

        // Phase 1 — measure every method's touches. No verdicts yet: the section dependency graph is
        // a property of the whole solution, and a verdict that consults it cannot be reached until
        // every method has been counted.
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
        // winning section is the concrete destination — which is what makes a collision claim sayable,
        // since C# only forbids duplicate signatures within one containing type.
        Dictionary<string, Dictionary<INamedTypeSymbol, int>> BehaviorTypes,
        List<string> TouchedSections,
        // The containing type's type parameters this method mentions, or null. They pin it in place:
        // a parameter declared on the enclosing type is scoped to that type, so no destination can
        // declare the method as written.
        string? OuterTypeParameters = null);

    /// <summary>
    /// Fan-in and fan-out per section, counted in distinct sections rather than in touches: the
    /// question is how much of the solution depends on a section, not how chatty any one caller is.
    /// </summary>
    /// <remarks>
    /// Built from every measured method, including the ones no verdict will mention. Restricting it to
    /// reportable findings would let the threshold depend on the thresholds — a section whose
    /// consumers all touch it twice would read as depended-upon by nobody.
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

    /// <summary>
    /// A section the rest of the solution depends on and which depends on almost none of it. "Move this
    /// into the foundation" is not advice — it is a description of what shared infrastructure looks
    /// like from the outside.
    /// </summary>
    /// <summary>
    /// Whether a section is shared infrastructure: depended on by many, depending on few.
    /// </summary>
    /// <remarks>
    /// A ratio of zero or less turns the category OFF. Without that guard <c>FanOut * 0</c> is zero and
    /// every section with enough fan-in compares true, so <c>--foundation-ratio 0</c> would mark
    /// everything as foundation and suppress the actionable findings — the exact opposite of what asking
    /// for no foundation detection means.
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

            // `nameof(Foo.Bar)` binds to Bar but executes nothing. Counting it as a touch would make
            // a logging statement look like a call into the section it names.
            // Names only, and each name once. A MemberBindingExpression (the `Method` in `x?.Method()`)
            // is NOT listed: its own `.Name` is an identifier that this walk already visits, so counting
            // the binding too scored every null-conditional call twice — inflating the target's side of
            // a comparison whose whole job is to weigh one section against another.
            // Object creation carries its constructor on the ObjectCreationExpression, not on the type
            // name inside it — the name binds to the TYPE. Without this, `new ForeignThing()` counted as
            // nothing at all while `_dep.Create()` counted as one, which is an artifact of the filter
            // rather than a decision: constructing another section's types is working with that section.
            // Element access carries its indexer on the ElementAccessExpression, the same way object
            // creation carries its constructor: `table[0]` has no identifier that binds to the indexer,
            // so three reads through an indexer measured as nothing at all.
            // ElementBindingExpression is the `[0]` in `table?[0]` — the conditional-access counterpart of
            // ElementAccessExpression, and a separate node type, so handling only the latter missed
            // null-safe indexer reads the same way the conduit walk once missed `?.` calls.
            // A user-defined operator hangs off the OPERATOR expression, the same way a constructor hangs
            // off the creation expression and an indexer off the element access: `left + right` has no
            // identifier that binds to `op_Addition`, so working on a foreign section through its
            // operators measured as nothing at all. Conversions included — `(int)weight` can be a call.
            if (node is not (IdentifierNameSyntax or GenericNameSyntax
                or BaseObjectCreationExpressionSyntax
                or ElementAccessExpressionSyntax or ElementBindingExpressionSyntax
                or BinaryExpressionSyntax or PrefixUnaryExpressionSyntax or PostfixUnaryExpressionSyntax
                or AssignmentExpressionSyntax or CastExpressionSyntax)) continue;
            if (IsInsideNameOf(node)) continue;

            // A type name inside `new T(...)` would otherwise be counted a second time, once as the
            // type and once as the constructor.
            if (node is (IdentifierNameSyntax or GenericNameSyntax)
                && node.Parent is ObjectCreationExpressionSyntax creation && creation.Type == node) continue;

            var touched = doc.Model.GetSymbolInfo(node, ct).Symbol;
            if (touched is null) continue;
            if (touched is not (IMethodSymbol or IPropertySymbol or IFieldSymbol or IEventSymbol)) continue;

            // Operator-shaped nodes are only a touch when the operator is USER-DEFINED. `int + int`
            // resolves to a builtin on System.Int32 and `x as T` to nothing; `_dep!` and a reference
            // cast are wrappers this walk already reads through elsewhere, not calls.
            if (node is BinaryExpressionSyntax or PrefixUnaryExpressionSyntax or PostfixUnaryExpressionSyntax
                    or AssignmentExpressionSyntax or CastExpressionSyntax
                && touched is not IMethodSymbol
                    { MethodKind: MethodKind.UserDefinedOperator or MethodKind.Conversion }) continue;

            // A local function's body is walked here too, but the local function symbol itself is
            // not another type's member and carries no section.
            var owner = touched.ContainingType;
            if (owner is null) continue;

            var section = SectionOf(owner, sectionByAssembly);
            if (section is null) continue;

            if (string.Equals(section, ownSection, StringComparison.OrdinalIgnoreCase))
            {
                // A field or property that only exists to reach another section is a conduit, not
                // state this method works on. Counted as own state it made delegation invisible: in
                // `_dep.Method()` the receiver scores 1 at home and the call scores 1 away, so a
                // method that does nothing BUT delegate ties 1:1 and can never out-touch its own
                // section. The receiver is dropped from both sides rather than moved to the target,
                // which would double the target count and shift every threshold with it.
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

    /// <summary>
    /// The containing type's type parameters that this method mentions, or null if it mentions none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A type parameter declared on the <b>containing</b> type is scoped to that type. A non-generic
    /// destination cannot declare <c>Source&lt;T&gt;.M(T)</c> at all, and a generic one would have to
    /// supply a parameter of its own — either way the method cannot move on its own, which is the same
    /// thing an implemented interface member means and is reported the same way.
    /// </para>
    /// <para>
    /// A method's OWN type parameters travel with it and are not pins — only
    /// <see cref="TypeParameterKind.Type"/> counts.
    /// </para>
    /// <para>
    /// This reads the whole declaration, so it sees the signature and every explicit mention in the
    /// body. It does not see a parameter reached only by inference (<c>var row = _rows[0];</c> where
    /// <c>_rows</c> is <c>List&lt;T&gt;</c>) — but such a body also works on its own type's state,
    /// which nothing here claims to relocate either.
    /// </para>
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
            // No dominance test. An orchestrator is defined by its reach, and requiring it to also
            // out-touch its own section would only report the ones that happen to hold no state --
            // which is not the property being described.
            // The per-section split is part of the evidence, not decoration: it is what separates a
            // method that spreads evenly over its sections from one that leans on a single one, and at
            // a fan-out of two that difference is the whole reading.
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

        // Reads of another section's DTO properties are how mapping code looks from here, and a
        // mapper belongs to whoever needs the mapped shape. Only calls into the target's behavior
        // argue that the target should own the method.
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

        // The other way a method is pinned to its type: it uses a type parameter the CONTAINING type
        // declares. That parameter does not exist at any destination, so the relocation is a generic
        // redesign rather than a move, and reporting it as a plain move overstates what it would take.
        if (m.OuterTypeParameters is { } pinned)
            return Finding(m, target, behaviorTouches, dataTouches, MisplacedVerdict.Blocked, evidence,
                blockedBy: $"uses type parameter {pinned} of {m.Symbol.ContainingType.OriginalDefinition.ToDisplayString()}, " +
                    "which is scoped to that type and does not exist at the destination");

        // The concrete type the method leans on hardest in the winning section. This is what makes a
        // collision claim sayable at all: a duplicate signature is only prohibited within one containing
        // type, so "the destination cannot take both" needs a destination narrower than an assembly.
        var destination = DestinationType(m, target);
        var collision = destination is null ? null : Collision(m.Symbol, destination);

        return Finding(m, target, behaviorTouches, dataTouches,
            collision is null ? MisplacedVerdict.Move : MisplacedVerdict.MoveWouldDuplicate,
            evidence, duplicateOf: collision, destinationType: QualifiedName(destination));
    }

    /// <summary>
    /// What the destination already declares under this method's name, described precisely enough to act
    /// on: whether the signature matches, and if not, how it differs.
    /// </summary>
    /// <remarks>
    /// This deliberately stops short of claiming the two methods DO the same thing — that needs the
    /// bodies compared, and a name plus a signature is not a proof of equivalence. What it does buy is a
    /// real distinction the name-only check could not make: an identical signature means the move cannot
    /// be a straight relocation, because the destination will not compile with both. A namesake with
    /// different parameters is a weaker signal, reported as such rather than as the same finding.
    /// </remarks>
    private static string? Collision(IMethodSymbol moving, INamedTypeSymbol destination)
    {
        var candidates = destination.GetMembers(moving.Name)
            .OfType<IMethodSymbol>()
            .Where(m => m.MethodKind == MethodKind.Ordinary)
            .ToList();
        if (candidates.Count == 0) return null;

        // An exact signature match first: that one is decisive, so it must not be hidden behind a
        // near-miss that happens to be enumerated earlier.
        foreach (var candidate in candidates)
            if (SameSignature(moving, candidate))
                return $"{Describe(candidate)} — same signature, so {destination.Name} cannot declare both";

        var namesake = candidates[0];
        var difference = moving.Parameters.Length == namesake.Parameters.Length
            ? "same arity, different parameter types"
            : $"{namesake.Parameters.Length} parameter(s) against this method's {moving.Parameters.Length}";
        return $"{Describe(namesake)} — {difference}, so reconcile the two rather than copying either";
    }

    /// <summary>
    /// The type in <paramref name="target"/> this method calls most — the natural host if it moves.
    /// </summary>
    /// <remarks>
    /// Only called types count, never data carriers: a method that reads another section's DTOs is a
    /// mapper and is judged as one before this is reached. The answer may be an INTERFACE, which cannot
    /// host a body — but the collision question is still well posed against it, since an interface cannot
    /// declare two members of the same signature either, and naming it tells the reader exactly which
    /// contract the move would run into.
    /// </remarks>
    private static INamedTypeSymbol? DestinationType(MethodTouches m, string target)
    {
        if (!m.BehaviorTypes.TryGetValue(target, out var byType) || byType.Count == 0) return null;

        INamedTypeSymbol? best = null;
        int bestCount = 0;
        foreach (var (type, count) in byType)
        {
            // Ties break on name so the same solution always reports the same destination: iteration
            // order over a symbol-keyed dictionary is not something to build output on.
            if (count > bestCount ||
                (count == bestCount && best is not null &&
                 string.CompareOrdinal(type.Name, best.Name) < 0))
            {
                (best, bestCount) = (type, count);
            }
        }
        return best;
    }

    /// <summary>
    /// Whether a parameter is passed by reference, in the sense that matters to overload resolution.
    /// </summary>
    /// <remarks>
    /// C# refuses two declarations that differ only in <c>ref</c> vs <c>out</c> vs <c>in</c> (CS0663), so
    /// all of them collapse to one answer here: by reference, or by value. Comparing the enum exactly
    /// rejected such a pair as a near-miss and then reported a decisive collision as "different parameter
    /// types" — the same class of error as comparing return types.
    /// </remarks>
    private static bool IsByReference(RefKind kind) => kind != RefKind.None;

    /// <summary>
    /// Whether this touch is a data read rather than a behavior call: the touched member carries data on
    /// a type the active config classifies as a DTO, or the type's shape says it carries data and
    /// nothing else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The union is the same one <see cref="SectionShapeAnalyzer"/> and <see cref="CanonicalReadDtoSet"/>
    /// already take. A config rule is a deliberate statement about a type and outranks a heuristic that
    /// did not recognise it; the structural test still stands alone, because a section-only config
    /// carries no <c>dto</c> rule at all and the split has to work without one.
    /// </para>
    /// <para>
    /// The <paramref name="touched"/> member only matters on the <b>configured</b> side, and it matters
    /// there because the two tests establish different things. A config rule labels a type's role and
    /// says nothing about its members, so a configured DTO can declare methods — calling one is a
    /// behavior call however the type is labelled, and treating it as a read reported a method calling
    /// three of them as a mapper. The structural test needs no such guard: it rejects any type exposing
    /// behavior at all, so on a type that passes it there is no behavior to miscount.
    /// </para>
    /// </remarks>
    private static bool IsData(
        INamedTypeSymbol type, INamedTypeSymbol? through, ISymbol touched, HashSet<string> configuredDtos) =>
        // An enum has no behavior to call. The structural test excludes enums only because it gates on
        // class-or-struct, which is a gap in that test rather than a claim that an enum is behavior:
        // reading `SpaceSize.Large` is reading a constant. Counted as calls, enum members made the
        // evidence line say "11 call(s) into Camps" where nothing is called, and let an enum WIN the
        // destination contest -- proposing that a method be moved onto a type that cannot declare one.
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
    /// <para>
    /// Needed because a member's containing type is where it is <b>declared</b>, not what the caller is
    /// holding. A configured DTO that inherits <c>Id</c> from a base the config does not name reports
    /// that base as the containing type, so <c>result.Id</c> read as a behavior call on the base and
    /// could turn a mapper into a move.
    /// </para>
    /// <para>
    /// Used only for the configured-DTO test. The section a touch is attributed to still comes from the
    /// declaring type, which is the existing treatment of every inherited member and is not narrowed to
    /// DTOs here.
    /// </para>
    /// </remarks>
    private static INamedTypeSymbol? AccessedThrough(SyntaxNode node, SolutionDocument doc, CancellationToken ct)
    {
        // An indexer read is the access, so it carries its own receiver rather than hanging off a
        // parent: `row[0]` keeps it on the element access and `row?[0]` on the enclosing conditional
        // access. Resolved only from the parent, an indexer inherited by a configured DTO was judged
        // by its declaring base — the same miss that once made inherited PROPERTIES read as behavior.
        var receiver = node switch
        {
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
        // `row is { Id: > 0 }` binds `Id` to the property with no receiver expression anywhere near it:
        // the receiver is the pattern's INPUT. Unresolved, reads of a configured DTO written as a
        // property pattern were judged by whichever base declares the property.
        if (receiver is null)
            return PatternInputType(node, doc, ct)?.OriginalDefinition as INamedTypeSymbol;

        return doc.Model.GetTypeInfo(receiver, ct).Type?.OriginalDefinition as INamedTypeSymbol;
    }

    /// <summary>
    /// The type a property-pattern subpattern is matched against, or null if this node is not one.
    /// </summary>
    /// <remarks>
    /// The input is stated in one of four places, and the nearest one wins: the pattern's own type
    /// (<c>is Row { … }</c>), the enclosing subpattern's property (<c>{ Row: { … } }</c>), or the
    /// expression the whole pattern matches — an <c>is</c> operand, a switch expression's governing
    /// expression, or a switch statement's.
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

    /// <summary>
    /// Whether two methods collide as C# declarations.
    /// </summary>
    /// <remarks>
    /// The RETURN TYPE is deliberately not compared. C# does not permit overloading on return type, so two
    /// methods alike in name and parameters but differing in what they return are a compile-time collision,
    /// not a pair of overloads — and comparing it made the analyzer reject the match and then report
    /// "different parameter types", which was doubly wrong: the wrong verdict and a false reason.
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

    /// <summary>
    /// Whether two parameter types are the same type <i>for signature purposes</i>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A method's own type parameters are distinct symbols per method, so <c>Map&lt;T&gt;(T value)</c>
    /// and <c>Map&lt;U&gt;(U value)</c> compare unequal under
    /// <see cref="SymbolEqualityComparer.Default"/> — while C# reads them as the same signature and
    /// refuses to declare both. Matched by <see cref="ITypeParameterSymbol.Ordinal"/> instead, which is
    /// how the language identifies them.
    /// </para>
    /// <para>
    /// Only <see cref="TypeParameterKind.Method"/> parameters are matched this way. A type-level
    /// parameter belongs to the class, and the class is not moving: <c>Foo&lt;T&gt;.M(T)</c> put on
    /// <c>Bar&lt;U&gt;</c> would not compile with <c>T</c> at all, so symbol equality — which fails
    /// here — is the answer that keeps the collision claim true.
    /// </para>
    /// <para>
    /// The walk is structural because a type parameter can be nested: <c>Map(List&lt;T&gt; values)</c>
    /// and <c>Map(IReadOnlyList&lt;T&gt; values)</c> differ, while <c>Map&lt;T&gt;(List&lt;T&gt;)</c> and
    /// <c>Map&lt;U&gt;(List&lt;U&gt;)</c> do not. Nullable annotation is deliberately not compared: it
    /// is not part of a C# signature, and two members differing only in it still collide.
    /// </para>
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

        // A function pointer's identity is its whole signature, RETURN TYPE INCLUDED — unlike a
        // method's, which is why this compares what SameSignature deliberately does not. Without it
        // `delegate*<T, void>` and `delegate*<U, void>` fell through to symbol equality and read as
        // different, turning a decisive collision into a near-miss.
        if (a is IFunctionPointerTypeSymbol funcA)
        {
            if (b is not IFunctionPointerTypeSymbol funcB) return false;
            var (sigA, sigB) = (funcA.Signature, funcB.Signature);
            if (sigA.Parameters.Length != sigB.Parameters.Length) return false;
            if (sigA.CallingConvention != sigB.CallingConvention) return false;
            if (sigA.RefKind != sigB.RefKind) return false;
            if (!SameParameterType(sigA.ReturnType, sigB.ReturnType)) return false;
            for (int i = 0; i < sigA.Parameters.Length; i++)
            {
                if (sigA.Parameters[i].RefKind != sigB.Parameters[i].RefKind) return false;
                if (!SameParameterType(sigA.Parameters[i].Type, sigB.Parameters[i].Type)) return false;
            }
            return true;
        }

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
    /// The walk climbs out of the member-access chain to find the access for which this node is the
    /// <i>receiver</i>, so <c>this._dep.Method()</c> is read the same as <c>_dep.Method()</c>. When the
    /// node is the member <i>being</i> accessed rather than the receiver, it is a real touch and the
    /// walk reports false.
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

                // `_dep?.Method()` puts the receiver under a conditional access and the invoked name
                // under a member BINDING on the other side of the `?.`, so the member-access walk never
                // reaches it. Null-safe delegation is common enough that missing it left the same 1:1 tie
                // the conduit rule exists to break.
                // `_dep?.Method()` puts the receiver under a conditional access and the reached member
                // on the other side of the `?.`, as a member binding for `?.Method` and an ELEMENT
                // binding for `?[0]`. Searching only for the former left `_slots?[0]` scoring its
                // receiver as own state three times, tying 3:3 against the indexer reads and hiding
                // the same delegation the non-conditional `_slots[0]` reports.
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
                // conduit as in `_dep.Method()`. Unrecognised, three reads through a held indexer scored
                // 3 own against 3 target and tied — the exact shape the conduit rule exists to break.
                // The element access IS the access, so it is what gets resolved rather than a name.
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

            // Only keep climbing while there is a wrapper that leaves this node the receiver. `_dep!` is
            // such a wrapper: the null-forgiving operator changes nothing about what is being reached,
            // and `_dep!.Method()` is the same delegation as `_dep.Method()`. So is a cast — reaching
            // another section through `((IForeign)_dep).Method()` is the same delegation as through
            // `_dep.Method()`, and stopping at the cast scored the receiver as own state and tied.
            bool transparent = current.Parent switch
            {
                MemberAccessExpressionSyntax or ParenthesizedExpressionSyntax => true,
                PostfixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.SuppressNullableWarningExpression } => true,
                CastExpressionSyntax cast => cast.Expression == current,
                // `(await _depTask).Method()` — awaiting a held task changes nothing about what is
                // reached, so the field is as much a conduit as in the synchronous form.
                AwaitExpressionSyntax await => await.Expression == current,
                // `_dep as IForeign` — the same conversion written as an operator, and only the left
                // side is the value being converted; the right side is a type name.
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
    /// The contract that pins a method to its type — an interface member it implements, or a base
    /// member it overrides. Such a method cannot move alone: the declaration would have to move too,
    /// which is a different and larger change than relocating a file.
    /// </summary>
    private static string? Contract(IMethodSymbol symbol, Dictionary<string, string> inheritedContracts)
    {
        // A default interface method is not bound BY a contract, it IS one. Neither branch below catches
        // it — an override it is not, and `AllInterfaces` excludes the interface a member is declared on —
        // so a target-dominant default body was reported as a plain move, when relocating it changes what
        // every implementer inherits.
        if (symbol.ContainingType.TypeKind == TypeKind.Interface)
            return $"declared on the interface {symbol.ContainingType.Name}";

        // The implementation half of a partial method. Only this half is ever measured — the defining
        // half has no body, so the body check in AnalyzeAsync skips it — and it cannot travel alone:
        // C# requires both halves in the same containing type, so relocating the body without the
        // declaration does not compile. The pin is the declaration, exactly as for an interface member.
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

        // Last, because it is the only branch needing an index: the contract may belong to a type further
        // down the hierarchy rather than to this one.
        return inheritedContracts.TryGetValue(MethodKey(symbol.OriginalDefinition), out var inherited)
            ? inherited
            : null;
    }

    /// <summary>
    /// Namespace-qualified name of the destination type, or null when none was chosen.
    /// </summary>
    /// <remarks>
    /// Qualified rather than simple because a section can hold two types of one name in different
    /// namespaces, and nothing else in the output identifies which was meant — no destination file or
    /// namespace is reported anywhere. The section is still readable from the evidence line, so the
    /// qualified name costs the reader nothing it does not already say.
    /// </para>
    /// <para>
    /// <see cref="ISymbol.ToDisplayString()"/> rather than namespace-plus-name assembled by hand, which
    /// was the first attempt: hand assembly drops the containing types, so <c>OuterA.Handler</c> and
    /// <c>OuterB.Handler</c> in one namespace both rendered as <c>Ns.Handler</c>, and it drops generic
    /// arity too. The default display format keeps both, and is the same form
    /// <see cref="SolutionClassifier.TypeKey"/> already keys on.
    /// </remarks>
    private static string? QualifiedName(INamedTypeSymbol? type) => type?.ToDisplayString();

    /// <summary><c>1 touch</c> / <c>4 touches</c>. The output is read by people and by models.</summary>
    private static string Touches(int n) => n == 1 ? "1 touch" : $"{n} touches";

    private static string? SectionOf(INamedTypeSymbol type, Dictionary<string, string> sectionByAssembly) =>
        type.ContainingAssembly?.Name is { } assembly && sectionByAssembly.TryGetValue(assembly, out var section)
            ? section
            : null;

    /// <summary>
    /// Methods that implement an interface member <b>for some type other than the one declaring them</b>,
    /// mapped to the description that says so.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>class Derived : Base, IFoo</c> can satisfy <c>IFoo.M</c> with an inherited <c>Base.M</c>. Asked
    /// from <c>Base</c>, there is no interface to find — <c>Base.AllInterfaces</c> is empty — so the
    /// method reads as movable while moving it would leave <c>Derived</c> without an implementation.
    /// The relationship is only visible from the derived type, so it has to be indexed from every type
    /// rather than looked up per method.
    /// </para>
    /// <para>
    /// Keyed by a string rather than by symbol on purpose: types are collected from each project's own
    /// compilation, and the symbol a document's model produces for the same method in a referencing
    /// project is a different instance that <see cref="SymbolEqualityComparer"/> would miss.
    /// </para>
    /// <para>
    /// Walks <b>every</b> type in each analyzed compilation rather than the classified list, which drops
    /// effectively private types. A private or privately-nested <c>Derived : Base, IFoo</c> constrains a
    /// public <c>Base.M</c> exactly as a public one does — the compiler does not care who can see the
    /// implementer — so reading the filtered list left that pin invisible.
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
                // project re-walked its whole dependency closure — interface mapping included — and the
                // pass scaled with repeated references rather than with the solution's distinct types.
                // Each analyzed assembly still visits its own types, so no implementer goes unseen.
                if (!SymbolEqualityComparer.Default.Equals(type.ContainingAssembly, compilation.Assembly))
                    continue;

                foreach (var iface in type.AllInterfaces)
                    foreach (var member in iface.GetMembers())
                    {
                        if (member is not IMethodSymbol candidate) continue;
                        if (type.FindImplementationForInterfaceMember(candidate) is not IMethodSymbol impl) continue;

                        // Declared on this very type is the case Contract already answers without an index.
                        if (SymbolEqualityComparer.Default.Equals(impl.ContainingType, type)) continue;

                        // The ORIGINAL definition. A contract supplied through a constructed base —
                        // `Derived : Base<int>, IFoo` inheriting `Base<T>.M(T)` — resolves to the
                        // substituted `Base<int>.M(int)`, while the method measured from syntax is
                        // `Base<T>.M(T)`. Keyed as substituted, the two never meet and the pin stays
                        // invisible, which is the bug this index was added to fix.
                        index.TryAdd(
                            MethodKey(impl.OriginalDefinition),
                            $"implements {iface.Name}.{candidate.Name} for {type.Name}");
                    }

                // The other direction of the same blindness. `Contract` sees that a method IS an
                // override; nothing on a base declaration says it IS OVERRIDDEN, so a virtual pipe with
                // a derived override read as freely movable — and moving it leaves the override with
                // nothing to override. Only the immediate base needs recording here: in a three-deep
                // chain the middle declaration is itself an override and pins its own base when its
                // type is visited.
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
    /// Identifies a method across compilations: declaring type, name, and parameter types. Parameter
    /// types are included because two overloads of one name need not have the same answer — one may
    /// implement an interface member while the other does not.
    /// </summary>
    private static string MethodKey(IMethodSymbol method) =>
        $"{SolutionClassifier.TypeKey(method.ContainingType)}|{method.Name}|" +
        // The ref kind belongs in the key even though it is deliberately IGNORED when asking whether two
        // declarations collide. Those are opposite questions: collision asks "could one type declare
        // both?", where ref and out are the same answer, while this key asks "which method is this?",
        // where `M(int)` and `M(ref int)` are two distinct methods. Omitted, a contract pinning the `ref`
        // overload also pinned the by-value one, blocking a method nothing constrains.
        string.Join(",", method.Parameters.Select(p => $"{p.RefKind}:{p.Type.ToDisplayString()}")) +
        $"|{method.TypeParameters.Length}";

    /// <summary>
    /// Whether the node sits inside a <c>nameof(...)</c> argument. <c>nameof</c> is not a keyword, so
    /// the test is on the invoked identifier.
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
