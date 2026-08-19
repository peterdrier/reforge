using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

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
    /// The method reaches three or more other sections. That is not misplacement — no single section
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
    Blocked,

    /// <summary>Two other sections, or one without a clear majority. A human call.</summary>
    Judgment
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
    string? BlockedBy);

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
/// Three populations come out of one walk, and keeping them apart is the whole design:
/// </para>
/// <list type="bullet">
///   <item><b>Pipes</b> reach exactly one other section and concentrate on it. These are the
///         actionable ones: there is a named destination.</item>
///   <item><b>Orchestrators</b> reach three or more. Nothing is misplaced — the method exists
///         precisely to join sections — so no destination is proposed. They are reported because
///         the shape is also what an accidental junction drawer looks like.</item>
///   <item><b>Judgment</b> is everything between: two sections, or one without a majority.</item>
/// </list>
/// <para>
/// An orchestrator is <b>structurally invisible</b> to the classic envy predicate (touches
/// concentrated on one parameter, exceeding the method's own state), because spreading touches over
/// three sections is the opposite of concentrating them. That is why placement here is decided by
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

    /// <summary>Distinct other sections at which a method is called an orchestrator, not a pipe.</summary>
    public const int OrchestratorFanOut = 3;

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

        // Method names each section already declares, so a "move it there" recommendation can say
        // whether the destination has one of these already. Built from the classified corpus (all
        // types, every accessibility) rather than from exported surface only: a duplicate is a
        // duplicate whether or not the existing one is public.
        // Every method each section already declares, indexed by name, so a proposed move can be checked
        // against what is waiting at the destination. Keyed by name and not by signature because the
        // question at the destination is "is there already something called this", and the answer is
        // useful whether or not the parameters line up — the SHAPE comparison happens at judging time,
        // which is what separates a genuine collision from a namesake.
        var methodsBySection =
            new Dictionary<string, Dictionary<string, List<IMethodSymbol>>>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in classified)
        {
            ct.ThrowIfCancellationRequested();
            var bucket = methodsBySection.TryGetValue(c.Group, out var existing)
                ? existing
                : methodsBySection[c.Group] = new Dictionary<string, List<IMethodSymbol>>(StringComparer.Ordinal);
            foreach (var m in c.Type.GetMembers())
            {
                if (m is not IMethodSymbol { MethodKind: MethodKind.Ordinary } method) continue;
                if (bucket.TryGetValue(method.Name, out var sameName)) sameName.Add(method);
                else bucket[method.Name] = [method];
            }
        }

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

                var touches = Measure(symbol, declaration, doc, ownSection, sectionByAssembly, solutionDirectory, ct);
                if (touches is not null) measured.Add(touches);
            }
        }

        var sections = BuildSectionGraph(measured, sectionByAssembly);

        // Phase 2 — verdicts, now that "is this target a foundation?" is answerable.
        var findings = measured
            .Select(m => Judge(m, sections, methodsBySection, foundationRatio))
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
        List<string> TouchedSections);

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
    private static bool IsFoundation(SectionDependencyProfile p, int ratio) =>
        p.FanIn >= FoundationMinimumFanIn && p.FanIn >= p.FanOut * ratio;

    private static MethodTouches? Measure(
        IMethodSymbol symbol,
        MethodDeclarationSyntax declaration,
        SolutionDocument doc,
        string ownSection,
        Dictionary<string, string> sectionByAssembly,
        string solutionDirectory,
        CancellationToken ct)
    {
        int ownTouches = 0;
        var behavior = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var data = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        SyntaxNode body = declaration.Body ?? (SyntaxNode)declaration.ExpressionBody!;
        foreach (var node in body.DescendantNodesAndSelf())
        {
            ct.ThrowIfCancellationRequested();

            // `nameof(Foo.Bar)` binds to Bar but executes nothing. Counting it as a touch would make
            // a logging statement look like a call into the section it names.
            if (node is not (IdentifierNameSyntax or GenericNameSyntax or MemberBindingExpressionSyntax)) continue;
            if (IsInsideNameOf(node)) continue;

            var touched = doc.Model.GetSymbolInfo(node, ct).Symbol;
            if (touched is null) continue;
            if (touched is not (IMethodSymbol or IPropertySymbol or IFieldSymbol or IEventSymbol)) continue;

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

            var bucket = CanonicalReadDtoSet.IsDataCarrier(owner.OriginalDefinition) ? data : behavior;
            bucket[section] = bucket.TryGetValue(section, out var n) ? n + 1 : 1;
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
            touchedSections);
    }

    private static MisplacedMethod? Judge(
        MethodTouches m,
        Dictionary<string, SectionDependencyProfile> sections,
        Dictionary<string, Dictionary<string, List<IMethodSymbol>>> methodsBySection,
        int foundationRatio)
    {
        if (m.TouchedSections.Count >= OrchestratorFanOut)
        {
            // No dominance test. An orchestrator is defined by its reach, and requiring it to also
            // out-touch its own section would only report the ones that happen to hold no state --
            // which is not the property being described.
            int reached = m.Behavior.Values.Sum() + m.Data.Values.Sum();
            return Finding(m, null, 0, 0, MisplacedVerdict.Orchestrator,
                $"reaches {m.TouchedSections.Count} other sections in {Touches(reached)} " +
                $"({string.Join(", ", m.TouchedSections)}); {Touches(m.OwnTouches)} on {m.Section}");
        }

        if (m.TouchedSections.Count > 1)
        {
            int reached = m.Behavior.Values.Sum() + m.Data.Values.Sum();
            var parts = m.TouchedSections.Select(s =>
                $"{s}:{(m.Behavior.TryGetValue(s, out var b) ? b : 0) + (m.Data.TryGetValue(s, out var d) ? d : 0)}");
            return Finding(m, null, 0, 0, MisplacedVerdict.Judgment,
                $"splits {Touches(reached)} across {string.Join(", ", parts)}; {Touches(m.OwnTouches)} on {m.Section}");
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

        if (Contract(m.Symbol) is { } blockedBy)
            return Finding(m, target, behaviorTouches, dataTouches, MisplacedVerdict.Blocked, evidence,
                blockedBy: blockedBy);

        // What is already at the destination under this name, and how close it is.
        var collision = Collision(m.Symbol, target, methodsBySection);

        return Finding(m, target, behaviorTouches, dataTouches,
            collision is null ? MisplacedVerdict.Move : MisplacedVerdict.MoveWouldDuplicate,
            evidence, duplicateOf: collision);
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
    private static string? Collision(
        IMethodSymbol moving, string target,
        Dictionary<string, Dictionary<string, List<IMethodSymbol>>> methodsBySection)
    {
        if (!methodsBySection.TryGetValue(target, out var byName)) return null;
        if (!byName.TryGetValue(moving.Name, out var candidates)) return null;

        // An exact signature match first: that one is decisive, so it should not be hidden behind a
        // near-miss that happens to be enumerated earlier.
        foreach (var candidate in candidates)
            if (SameSignature(moving, candidate))
                return $"{Describe(candidate)} — same signature, so the destination cannot take both";

        var namesake = candidates[0];
        var difference = moving.Parameters.Length == namesake.Parameters.Length
            ? "same arity, different parameter types"
            : $"{namesake.Parameters.Length} parameter(s) against this method's {moving.Parameters.Length}";
        return $"{Describe(namesake)} — {difference}, so reconcile the two rather than copying either";
    }

    private static bool SameSignature(IMethodSymbol a, IMethodSymbol b)
    {
        if (a.Parameters.Length != b.Parameters.Length) return false;
        if (a.TypeParameters.Length != b.TypeParameters.Length) return false;
        if (!SymbolEqualityComparer.Default.Equals(a.ReturnType, b.ReturnType)) return false;

        for (int i = 0; i < a.Parameters.Length; i++)
        {
            var (x, y) = (a.Parameters[i], b.Parameters[i]);
            if (x.RefKind != y.RefKind) return false;
            if (!SymbolEqualityComparer.Default.Equals(x.Type, y.Type)) return false;
        }
        return true;
    }

    private static string Describe(IMethodSymbol method) =>
        $"{method.ContainingType.Name}.{method.Name}({string.Join(", ", method.Parameters.Select(p => p.Type.Name))})";

    private static MisplacedMethod Finding(
        MethodTouches m, string? target, int behaviorTouches, int dataTouches,
        MisplacedVerdict verdict, string evidence, string? duplicateOf = null, string? blockedBy = null) =>
        new(m.Name, m.File, m.Line, m.Section, target, m.OwnTouches, behaviorTouches, dataTouches,
            m.TouchedSections, verdict, evidence, duplicateOf, blockedBy);

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
        while (current.Parent is MemberAccessExpressionSyntax parent)
        {
            // On the name side, not the receiver side: climb, so a qualified receiver still resolves.
            if (parent.Expression != current)
            {
                current = parent;
                continue;
            }

            var accessed = doc.Model.GetSymbolInfo(parent.Name, ct).Symbol;
            if (accessed?.ContainingType is not { } owner) return false;
            var section = SectionOf(owner, sectionByAssembly);
            return section is not null
                && !string.Equals(section, ownSection, StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    /// <summary>
    /// The contract that pins a method to its type — an interface member it implements, or a base
    /// member it overrides. Such a method cannot move alone: the declaration would have to move too,
    /// which is a different and larger change than relocating a file.
    /// </summary>
    private static string? Contract(IMethodSymbol symbol)
    {
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

        return null;
    }

    /// <summary><c>1 touch</c> / <c>4 touches</c>. The output is read by people and by models.</summary>
    private static string Touches(int n) => n == 1 ? "1 touch" : $"{n} touches";

    private static string? SectionOf(INamedTypeSymbol type, Dictionary<string, string> sectionByAssembly) =>
        type.ContainingAssembly?.Name is { } assembly && sectionByAssembly.TryGetValue(assembly, out var section)
            ? section
            : null;

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
