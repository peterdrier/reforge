using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Reforge;

/// <summary>
/// The shared type-classification pass: opens each non-test project, enumerates source-declared
/// types, resolves each into a section (group) and assigns role tags. Extracted from
/// SurfaceScoreEngine so surface-score, section-shape, and the baseline gate share one pass.
/// </summary>
/// <remarks>
/// Section identity is the type's <b>containing assembly</b> (see <see cref="AssemblySections"/>),
/// never config. An assembly boundary is structural and compiler-enforced — strictly stronger than
/// either a name glob or a path glob, and it cannot drift from the solution because it *is* the
/// solution. Config carries policy only; it no longer describes where sections are.
/// </remarks>
public static class SolutionClassifier
{
    public static async Task<IReadOnlyList<ClassifiedType>> ClassifyAsync(
        Solution solution, SurfaceScoreConfig config, string solutionDirectory, CancellationToken ct)
    {
        var seenByDisplay = new HashSet<string>(StringComparer.Ordinal);
        var projects = solution.Projects
            .Where(p => !p.Name.Contains("Test", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var analyzed = new HashSet<string>(projects.Select(p => p.AssemblyName), StringComparer.Ordinal);

        // Pass 1 — collect types with their raw assembly name. The section name can't be assigned
        // yet: it depends on the prefix shared across the assemblies that actually declare types.
        var collected = new List<(INamedTypeSymbol Type, string Assembly, HashSet<string> Tags, string File, Location Location)>();
        // Every non-interface source type in the analyzed set, private ones included. Only Pass 1.5
        // reads this; see the note at the point it is filled.
        var implementers = new List<INamedTypeSymbol>();

        foreach (var project in projects)
        {
            var compilation = await project.GetCompilationAsync(ct);
            if (compilation is null) continue;

            foreach (var type in EnumerateTypes(compilation.GlobalNamespace))
            {
                if (!type.Locations.Any(l => l.IsInSource)) continue;
                if (type.IsImplicitlyDeclared) continue;
                // A compilation's global namespace also reaches referenced projects' source types,
                // so the enumerating project is NOT the owner — the containing assembly is. Types
                // from assemblies outside the analyzed set (test projects reached by reference)
                // drop out here.
                var assembly = type.ContainingAssembly?.Name;
                if (assembly is null || !analyzed.Contains(assembly)) continue;
                // Dedup is per ASSEMBLY, not per display name. Every project's compilation reaches
                // its references' source types, so the same type is enumerated repeatedly and must
                // collapse — but two assemblies may legitimately declare the same fully qualified
                // name (an internal helper, a generated type). Keying on the name alone dropped the
                // second one, and an assembly whose every type collided vanished from the section
                // map entirely.
                if (!seenByDisplay.Add($"{assembly}|{type.ToDisplayString()}")) continue;

                // Private types are not corpus, but they ARE implementation evidence. An interface
                // whose only implementer is a private nested class behind a public factory is
                // implemented in this solution, and Pass 1.5 has to be able to read those bodies —
                // otherwise it concludes "unknown" for a type it is looking straight at. Held in a
                // separate list so nothing downstream mistakes them for scored types.
                if (type.TypeKind != TypeKind.Interface) implementers.Add(type);

                // Internal types stay in the corpus on purpose: their implementation is still
                // complexity the section carries, and the sizing rules must see it. What they no
                // longer do is score as surface — see ClassifiedType.IsExported.
                //
                // EFFECTIVE accessibility, not the declared modifier. A `public` type nested inside a
                // `private` one is private in every sense that matters, and the recursive walk above is
                // what first made it reachable — so checking only the declaration would have quietly
                // admitted a class of types the corpus never contained, and charged sections for them.
                if (IsEffectivelyPrivate(type)) continue;

                var primaryLocation = type.Locations.First(l => l.IsInSource);
                var filePath = primaryLocation.SourceTree?.FilePath ?? "";
                var relPath = LocationHelper.NormalizePath(filePath, solutionDirectory);
                var nsName = type.ContainingNamespace?.ToDisplayString() ?? "";

                collected.Add((type, assembly, Classify(config, type, relPath, nsName), relPath, primaryLocation));
            }
        }

        // Pass 1.5 — demote name-classified write surfaces that nothing actually writes through.
        DemoteReadOnlyServiceInterfaces(collected, implementers, ct);

        // Pass 2 — name the sections. Only type-declaring assemblies participate: a docs/tooling
        // project that ships no C# is not a section, and letting it into the prefix calculation
        // would strip nothing at all from the real ones.
        var sectionByAssembly = AssemblySections.Resolve(collected.Select(c => c.Assembly));

        return collected
            .Select(c => new ClassifiedType(c.Type, sectionByAssembly[c.Assembly], c.Tags, c.File, c.Location))
            .ToList();
    }

    /// <summary>
    /// Reclassifies an interface the name patterns called a full (write-capable) service interface as
    /// a <c>readServiceInterface</c> when no implementation of any of its members observably mutates
    /// persistent state.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>fullServiceInterface</c> is assigned by the name pattern <c>I*Service</c>, and the read
    /// escape hatch only catches <c>I*ServiceRead</c> / <c>I*ReadService</c> / <c>I*QueryService</c>.
    /// So an all-<c>Get*</c> facade named <c>IAuditViewerService</c> was priced as published <b>write</b>
    /// surface. On the Humans corpus that was 10 of 47 exported write interfaces — 20 methods — which
    /// is issue #54.
    /// </para>
    /// <para>
    /// For methods the test is behavioral, not nominal: it reuses
    /// <see cref="ImplementationComplexity.IsMutation"/>, which decides from the implementation body
    /// (a persistence-commit call) or the method's shape (returns no data, non-query verb). That is
    /// what makes this rename-proof, and it is why a method's own declaration cannot answer it — an
    /// interface method has no body, so the question is only decidable at the implementation.
    /// </para>
    /// <para>
    /// Two member shapes ARE decidable on the declaration and are checked there: a <b>settable
    /// property</b> and an <b>event</b>. Either hands every consumer a mutation directly, and no
    /// implementation body can withdraw it, so one of those is sufficient on its own — no
    /// implementation need be found at all.
    /// </para>
    /// <para>
    /// Membership is read from the interface <b>and its base interfaces</b>. <c>GetMembers()</c>
    /// returns only what a type declares itself, so <c>IOrderService : ICrudService&lt;Order&gt;</c>
    /// would look empty and demote while its consumers get a full set of writes.
    /// </para>
    /// <para>
    /// Demotion requires <b>evidence of read-only-ness</b>, not merely absence of evidence of writing.
    /// Two gaps therefore preserve the name-derived classification rather than repricing on nothing —
    /// silently repricing surface on an analysis gap is the failure this codebase has been bitten by
    /// before (see <see cref="SurfaceVisibility"/> and issue #51):
    /// </para>
    /// <list type="bullet">
    ///   <item><b>No implementation in the solution.</b> The walk skips test projects and cannot see
    ///         other assemblies, so "not found" means unknown, not read-only.</item>
    ///   <item><b>No implementing type accounts for the whole surface.</b> An abstract class may list
    ///         the interface and leave its members abstract; a bodyless data-returning declaration
    ///         reads as a query under the shape heuristic, which would demote on an absence. Evidence
    ///         of <i>writing</i> still counts from a partial observation — it is only the read-only
    ///         conclusion that needs every member accounted for by some one implementer.</item>
    /// </list>
    /// <para>
    /// Demoted interfaces are not exempted — they still score, at <c>readServiceInterfaceMethod</c>
    /// rather than <c>fullServiceInterfaceMethod</c>. A published read facade is real surface; it is
    /// just not a write commitment.
    /// </para>
    /// </remarks>
    private static void DemoteReadOnlyServiceInterfaces(
        List<(INamedTypeSymbol Type, string Assembly, HashSet<string> Tags, string File, Location Location)> collected,
        List<INamedTypeSymbol> implementers,
        CancellationToken ct)
    {
        var candidates = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var c in collected)
            if (c.Type.TypeKind == TypeKind.Interface && c.Tags.Contains("fullServiceInterface"))
                candidates[TypeKey(c.Type)] = c.Tags;
        if (candidates.Count == 0) return;

        var observed = new HashSet<string>(StringComparer.Ordinal);
        var incomplete = new HashSet<string>(StringComparer.Ordinal);
        var writes = new HashSet<string>(StringComparer.Ordinal);

        // Pass A -- everything decidable on the interface itself. A settable property or an event ON
        // the interface is already the write commitment: the declaration hands every consumer a
        // mutation, and no body could withdraw it. A non-abstract STATIC member is decidable here too,
        // because its body is right here — and it is callable with no instance and no implementing type.
        //
        // When that accounts for the WHOLE published surface, the interface is fully observed with no
        // implementer at all: `IClockService { static int GetTicks() => 0; }` publishes nothing but a
        // static query, so waiting for an implementation that will never exist would keep a definitively
        // read-only surface classified as a write.
        foreach (var c in collected)
        {
            if (c.Type.TypeKind != TypeKind.Interface) continue;
            var key = TypeKey(c.Type);
            if (!candidates.ContainsKey(key)) continue;

            bool wholeSurfaceDecidedHere = true;
            foreach (var member in PublishedMembers(c.Type))
            {
                ct.ThrowIfCancellationRequested();
                if (!IsReachableByConsumers(member)) continue;

                switch (DecideOnDeclaration(member, ct))
                {
                    case DeclarationVerdict.Write:
                        writes.Add(key);
                        break;
                    case DeclarationVerdict.NoWrite:
                        break;
                    default:
                        wholeSurfaceDecidedHere = false;
                        break;
                }

                if (writes.Contains(key)) break;
            }

            if (wholeSurfaceDecidedHere && !writes.Contains(key)) observed.Add(key);
        }

        // Pass B -- everything else is decided at the implementation.
        foreach (var type in implementers)
        {
            foreach (var iface in type.AllInterfaces)
            {
                ct.ThrowIfCancellationRequested();
                var key = TypeKey(iface.OriginalDefinition);
                if (!candidates.ContainsKey(key)) continue;

                bool complete = true;
                foreach (var member in PublishedMembers(iface))
                {
                    // Only what a consumer can reach. Since C# 8 an interface may declare private
                    // members, which no consumer can call and `ScoreInterfaceMethods` already excludes.
                    if (!IsReachableByConsumers(member)) continue;

                    // A static member whose behavior no implementer can replace carries its body on the
                    // interface itself, so it is decided in the declaration pass and there is nothing to
                    // look for here. `static abstract` and `static virtual` are both replaceable — a
                    // call through a constrained type parameter dispatches to the implementing type — so
                    // both fall through to the observation below.
                    if (member.IsStatic && !member.IsAbstract && !member.IsVirtual) continue;

                    switch (member)
                    {
                        case IMethodSymbol
                        {
                            MethodKind: MethodKind.Ordinary or MethodKind.UserDefinedOperator
                                        or MethodKind.Conversion
                        } m:
                            if (!ObserveMethod(type, m, key, writes, ct)) complete = false;
                            break;

                        // A getter cannot be judged by shape -- it returns data by definition -- but
                        // its body can still commit a write. Only the definitive signal applies, so
                        // this can add a write and never invent one.
                        case IPropertySymbol { GetMethod: not null } p:
                            if (!ObserveGetter(type, p, key, writes, ct)) complete = false;
                            break;

                        // Shapes that need no observation because something else already decided them.
                        // An accessor is decided with the property or event it belongs to; a nested
                        // type's members are its own surface, not this interface's; a `readonly` or
                        // `const` field cannot be written through; and a settable property, an event or
                        // a mutable field was already recorded as a write by the declaration pass, so
                        // the interface cannot demote whatever this loop concludes.
                        case IMethodSymbol
                        {
                            MethodKind: MethodKind.PropertyGet or MethodKind.PropertySet
                                        or MethodKind.EventAdd or MethodKind.EventRemove
                                        or MethodKind.EventRaise
                        }:
                        case INamedTypeSymbol:
                        case IFieldSymbol:
                        case IEventSymbol:
                        case IPropertySymbol:
                            break;

                        // Anything else is a shape this predicate has never seen. Demotion is a claim
                        // about the WHOLE published surface, so an unrecognized member is a gap and not
                        // a read: a C# feature added after this was written preserves the name-derived
                        // classification instead of quietly counting as harmless. Rounds 4 through 9 of
                        // review on #54 were all one shape at a time -- indexers, mutable fields, ref
                        // returns, static commands -- each defaulting to "read" until it was named.
                        default:
                            complete = false;
                            break;
                    }
                }

                // Completeness is required of every CONCRETE implementer, not of any one. An
                // interface with two implementations is only known to be read-only if both are
                // accounted for: one fully-read implementation says nothing about a second whose
                // members arrive from a referenced binary and could commit anything.
                //
                // Abstract classes are exempt from the requirement but not from the write scan. An
                // abstract class is not an implementation — it is a partial one whose gaps its
                // derived classes fill, and each of those is checked here in its own right. Holding
                // an abstract base to the same standard would block demotion for every interface
                // that has one.
                if (type.IsAbstract) continue;
                if (complete) observed.Add(key);
                else incomplete.Add(key);
            }
        }

        foreach (var (key, tags) in candidates)
        {
            if (!observed.Contains(key) || incomplete.Contains(key) || writes.Contains(key)) continue;
            tags.Remove("fullServiceInterface");
            tags.Add("readServiceInterface");
        }
    }

    /// <summary>
    /// Whether an interface member is part of the contract a consumer sees. Members without a modifier
    /// are implicitly public, so this is only false for the ones C# 8 added — <c>private</c> helpers and
    /// explicitly non-public default implementations, which no consumer can call and which
    /// <c>ScoreInterfaceMethods</c> already excludes.
    /// </summary>
    private static bool IsReachableByConsumers(ISymbol member) =>
        member.DeclaredAccessibility == Accessibility.Public;

    /// <summary>
    /// Whether a member hands consumers a mutation on the strength of its <b>declaration alone</b>, so
    /// that no implementation body could withdraw it.
    /// </summary>
    /// <remarks>
    /// Four shapes qualify:
    /// <list type="bullet">
    ///   <item>A <b>settable property</b> — including <c>init</c>, and including indexers — whose setter
    ///         a consumer can actually call. A default interface property may be
    ///         <c>int Value { get => 0; private set { } }</c>, which publishes no write at all.</item>
    ///   <item>An <b>event</b>, whose add/remove mutate the subscriber list.</item>
    ///   <item>A <b>mutable field</b>. An interface can declare a static field since C# 8, and
    ///         <c>IStateService.Current = 5</c> writes straight through it. <c>readonly</c> and
    ///         <c>const</c> do not qualify.</item>
    ///   <item>A member <b>returned by writable reference</b>. <c>ref int Current { get; }</c> has no
    ///         setter, but <c>svc.Current = 5</c> compiles and writes through to the backing state;
    ///         the implementation is <c>=&gt; ref _current</c>, which contains no persistence call and
    ///         so reads as a query. <c>ref readonly</c> does not qualify, which is why the test is
    ///         <c>ReturnsByRef</c> rather than a <c>RefKind</c> comparison — it is already false for
    ///         the readonly form.</item>
    /// </list>
    /// </remarks>
    private static bool PublishesWriteByDeclaration(ISymbol member) => member switch
    {
        // A setter only publishes a write if a consumer can call IT. A default interface property may
        // be `int Value { get => 0; private set { } }` — readable by everyone, settable by nobody
        // outside the interface. Both halves of the property are checked here rather than in sequence,
        // so a private setter cannot mask a writable ref return on the same member.
        IPropertySymbol p => (p.SetMethod is { } setter && IsReachableByConsumers(setter)) || p.ReturnsByRef,
        IMethodSymbol { ReturnsByRef: true } => true,
        IEventSymbol => true,
        IFieldSymbol { IsReadOnly: false, IsConst: false } => true,
        _ => false
    };

    /// <summary>What the interface's own declaration settles about one published member.</summary>
    private enum DeclarationVerdict
    {
        /// <summary>Publishes a mutation. No implementation could withdraw it.</summary>
        Write,

        /// <summary>Settled here, and it is not a write.</summary>
        NoWrite,

        /// <summary>Not decidable here: an implementing type has to supply the behavior.</summary>
        NeedsImplementation
    }

    /// <summary>
    /// Decides one published member from the interface alone. A non-abstract <c>static</c> member carries
    /// its body here, so unlike an instance member it needs no implementer — and it is callable:
    /// <c>IPurgeService.ClearAll()</c> needs no instance and no implementing type, which is why skipping
    /// every static member could let a published command go unseen. A <c>static abstract</c> member has
    /// no body here and is observed on the implementing type instead.
    /// </summary>
    private static DeclarationVerdict DecideOnDeclaration(ISymbol member, CancellationToken ct)
    {
        if (PublishesWriteByDeclaration(member)) return DeclarationVerdict.Write;

        switch (member)
        {
            // An accessor is settled with the property or event it belongs to; a nested type's members
            // are its own surface, not this interface's; and any field reaching here is `readonly` or
            // `const`, since a mutable one is a write above.
            case IMethodSymbol
            {
                MethodKind: MethodKind.PropertyGet or MethodKind.PropertySet or MethodKind.EventAdd
                            or MethodKind.EventRemove or MethodKind.EventRaise
            }:
            case INamedTypeSymbol:
                return DeclarationVerdict.NoWrite;

            // Any field reaching here is `readonly` or `const`, since a mutable one is a write above —
            // but its INITIALIZER runs on first access, so `static readonly int Blown = Db.SaveChanges();`
            // commits the moment a consumer touches it.
            case IFieldSymbol f:
                return FieldInitializerVerdict(f, ct);
        }

        if (!member.IsStatic || member.IsAbstract) return DeclarationVerdict.NeedsImplementation;

        // A `static virtual` default IS a body, so a mutating one publishes a write that no override can
        // withdraw. A read-only one settles nothing, because an override may write and calls through a
        // constrained type parameter reach it.
        if (member.IsVirtual)
            return StaticBodyVerdict(member, ct) == DeclarationVerdict.Write
                ? DeclarationVerdict.Write
                : DeclarationVerdict.NeedsImplementation;

        return StaticBodyVerdict(member, ct);
    }

    /// <summary>
    /// Reads the body a static interface member declares here. Methods and operators are judged by
    /// <see cref="ImplementationComplexity.IsMutation"/>; a getter only by the definitive signal, since it
    /// returns data by definition. A body that cannot be read at all is not an answer.
    /// </summary>
    private static DeclarationVerdict StaticBodyVerdict(ISymbol member, CancellationToken ct) =>
        member switch
        {
            // The body has to be readable. `IsMutation` falls back to the command shape when handed no
            // syntax, so a data-returning signature inherited from a referenced binary would answer
            // "read" on the strength of its name alone — the same gap the getter branch below refuses.
            IMethodSymbol
            {
                MethodKind: MethodKind.Ordinary or MethodKind.UserDefinedOperator or MethodKind.Conversion
            } m => MethodBody(m, ct) is { } body
                ? (ImplementationComplexity.IsMutation(m, body)
                    ? DeclarationVerdict.Write
                    : DeclarationVerdict.NoWrite)
                : DeclarationVerdict.NeedsImplementation,

            // A static getter is read exactly like an instance one: a getter returns data by definition,
            // so only a persistence commit in its body counts. A body that cannot be read is a gap.
            IPropertySymbol { GetMethod: not null } p => GetterBody(p, ct) switch
            {
                { } body when ImplementationComplexity.CommitsPersistentWrite(body) => DeclarationVerdict.Write,
                { } => DeclarationVerdict.NoWrite,
                _ => DeclarationVerdict.NeedsImplementation
            },

            _ => DeclarationVerdict.NeedsImplementation
        };

    /// <summary>
    /// Whether a <c>readonly</c> or <c>const</c> interface field's initializer commits. The field cannot
    /// be assigned through, but the first consumer access runs the initializer, so the write is published
    /// all the same. A <c>const</c> can only be a literal; a declaration with no readable syntax is from a
    /// referenced binary and is a gap, not a harmless field.
    /// </summary>
    private static DeclarationVerdict FieldInitializerVerdict(IFieldSymbol field, CancellationToken ct)
    {
        if (field.IsConst) return DeclarationVerdict.NoWrite;

        foreach (var reference in field.DeclaringSyntaxReferences)
        {
            if (reference.GetSyntax(ct) is not VariableDeclaratorSyntax declarator) continue;
            return declarator.Initializer?.Value is { } initializer
                   && ImplementationComplexity.CommitsPersistentWrite(initializer)
                ? DeclarationVerdict.Write
                : DeclarationVerdict.NoWrite;
        }

        return DeclarationVerdict.NeedsImplementation;
    }

    /// <summary>
    /// Reads one method's implementation on <paramref name="type"/>. Returns whether behavior was
    /// actually observed; records a write in <paramref name="writes"/> if it mutates.
    /// </summary>
    /// <remarks>
    /// An abstract or bodyless implementation is not an observation. An abstract class may list the
    /// interface and leave every member abstract, and the shape heuristic would then read a
    /// data-returning declaration as a read and demote on nothing — the concrete override may be in a
    /// skipped test project or another assembly. Evidence of writing counts from a partial observation
    /// either way; it is only the read-only conclusion that needs the whole surface accounted for.
    /// </remarks>
    private static bool ObserveMethod(
        INamedTypeSymbol type, IMethodSymbol member, string key, HashSet<string> writes, CancellationToken ct)
    {
        if (type.FindImplementationForInterfaceMember(member) is not IMethodSymbol mapped) return false;
        if (MostDerived(type, mapped) is not { IsAbstract: false } impl) return false;
        if (MethodBody(impl, ct) is not { } syntax) return false;

        if (ImplementationComplexity.IsMutation(impl, syntax)) writes.Add(key);
        return true;
    }

    /// <summary>
    /// The member an instance of <paramref name="type"/> actually runs for a mapped interface member:
    /// the most derived override of it, or the mapped member itself when nothing overrides it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>FindImplementationForInterfaceMember</c> answers with the entry in the interface map, which
    /// for <c>class Derived : Base</c> where <c>Base</c> declares the interface is a member of
    /// <c>Base</c> — and if that member is <c>abstract</c>, the answer has no body at all. Taken at
    /// face value that reads as "nothing observed", so <b>no interface implemented through an abstract
    /// base could ever be judged</b>. The abstract-base-plus-concrete-derived shape is common enough
    /// that the pass would have been close to inert on it.
    /// </para>
    /// <para>
    /// The walk starts at <paramref name="type"/> and climbs, so the first match is the most derived
    /// override — which is the one an instance of that type dispatches to.
    /// </para>
    /// </remarks>
    private static TSymbol? MostDerived<TSymbol>(INamedTypeSymbol type, TSymbol mapped)
        where TSymbol : class, ISymbol
    {
        if (mapped is { IsAbstract: false, IsVirtual: false }) return mapped;

        for (INamedTypeSymbol? t = type; t is not null; t = t.BaseType)
            foreach (var member in t.GetMembers(mapped.Name))
                if (member is TSymbol candidate && OverridesTransitively(candidate, mapped))
                    return candidate;

        return mapped;
    }

    /// <summary>Whether <paramref name="member"/> overrides <paramref name="target"/>, at any depth.</summary>
    private static bool OverridesTransitively(ISymbol member, ISymbol target)
    {
        for (var current = OverriddenBy(member); current is not null; current = OverriddenBy(current))
            if (SymbolEqualityComparer.Default.Equals(current, target)) return true;
        return false;

        static ISymbol? OverriddenBy(ISymbol s) => s switch
        {
            IMethodSymbol m => m.OverriddenMethod,
            IPropertySymbol p => p.OverriddenProperty,
            IEventSymbol e => e.OverriddenEvent,
            _ => null
        };
    }

    /// <summary>
    /// The declaration carrying a method's executable body, or null when none does.
    /// </summary>
    /// <remarks>
    /// Not simply <c>DeclaringSyntaxReferences[0]</c>. A <c>partial</c> method has two declarations —
    /// the defining one with a semicolon and the implementing one with the body — and Roslyn may
    /// enumerate the bodyless one first, which would read a fully implemented member as unobserved.
    /// <see cref="IMethodSymbol.PartialImplementationPart"/> is checked as well as every reference,
    /// so the body is found whichever symbol and whichever declaration holds it.
    /// </remarks>
    private static BaseMethodDeclarationSyntax? MethodBody(IMethodSymbol impl, CancellationToken ct)
    {
        foreach (var candidate in new[] { impl, impl.PartialImplementationPart })
        {
            if (candidate is null) continue;
            foreach (var reference in candidate.DeclaringSyntaxReferences)
                if (reference.GetSyntax(ct) is BaseMethodDeclarationSyntax syntax
                    && (syntax.Body is not null || syntax.ExpressionBody is not null))
                    return syntax;
        }
        return null;
    }

    /// <summary>
    /// Reads one property getter's implementation on <paramref name="type"/>, scanning it for a
    /// persistence commit. Returns whether the getter was accounted for.
    /// </summary>
    /// <remarks>
    /// An auto-property getter has no body at all, and that is a <b>complete</b> observation rather
    /// than a gap: a compiler-generated field read provably commits nothing. Only an abstract getter,
    /// or one whose declaration cannot be read, leaves the member unaccounted for. Note the asymmetry
    /// with <see cref="ObserveMethod"/> — a bodyless METHOD is abstract or partial and could do
    /// anything; a bodyless getter is an auto-property and can do nothing.
    /// </remarks>
    private static bool ObserveGetter(
        INamedTypeSymbol type, IPropertySymbol member, string key, HashSet<string> writes, CancellationToken ct)
    {
        if (type.FindImplementationForInterfaceMember(member) is not IPropertySymbol mapped) return false;
        if (MostDerived(type, mapped) is not { IsAbstract: false } impl) return false;
        if (impl.GetMethod is null) return true; // set-only: nothing to read, and a setter is a write anyway

        var body = GetterBody(impl, ct);
        if (body is not null)
        {
            if (ImplementationComplexity.CommitsPersistentWrite(body)) writes.Add(key);
            return true;
        }

        // No body found means one of two opposite things for a PROPERTY. Declared in SOURCE with no
        // body it is an auto-property, which provably commits nothing — a complete observation.
        // Reached from a referenced binary it has no syntax to read at all, so its getter could do
        // anything; that is a gap, and a gap must not read as read-only.
        //
        // An INDEXER is only ever the second of those: there is no auto-indexer, so one declared in
        // source always carries an accessor body or an arrow. Reaching here for an indexer means the
        // declaration could not be read, which is a gap however it is located.
        return !impl.IsIndexer && impl.Locations.Any(l => l.IsInSource);
    }

    /// <summary>
    /// The executable body behind a property getter, across the three shapes it can take: an
    /// <c>AccessorDeclarationSyntax</c> with a block or arrow, an expression-bodied property
    /// (<c>=&gt;</c> on the property itself), or an auto-property with no body at all.
    /// </summary>
    private static SyntaxNode? GetterBody(IPropertySymbol impl, CancellationToken ct)
    {
        // A partial PROPERTY splits the same way a partial method does: the defining declaration
        // carries no accessor body and the implementing one does. Both halves are searched, and the
        // implementation part first, so a bodyless definition never shadows a real getter.
        foreach (var property in new[] { impl.PartialImplementationPart, impl })
        {
            if (property is null) continue;

            foreach (var reference in (property.GetMethod?.DeclaringSyntaxReferences
                                       ?? ImmutableArray<SyntaxReference>.Empty))
            {
                var syntax = reference.GetSyntax(ct);
                if (syntax is AccessorDeclarationSyntax accessor)
                {
                    var body = (SyntaxNode?)accessor.Body ?? accessor.ExpressionBody?.Expression;
                    if (body is not null) return body;
                }
                else if (ArrowBody(syntax) is { } arrow) return arrow;
            }

            // An expression-bodied property's getter may report the PROPERTY as its declaration only
            // indirectly, so check the property's own syntax before concluding there is no body.
            foreach (var reference in property.DeclaringSyntaxReferences)
                if (ArrowBody(reference.GetSyntax(ct)) is { } arrow)
                    return arrow;
        }

        return null;
    }

    /// <summary>
    /// The expression behind a <c>=&gt;</c> on the member declaration itself. Properties and indexers
    /// both carry <c>ExpressionBody</c>, but each declares it on its own syntax type rather than on the
    /// shared <c>BasePropertyDeclarationSyntax</c>, so matching only <c>PropertyDeclarationSyntax</c>
    /// read every expression-bodied INDEXER as bodyless.
    /// </summary>
    private static ExpressionSyntax? ArrowBody(SyntaxNode? node) => node switch
    {
        PropertyDeclarationSyntax p => p.ExpressionBody?.Expression,
        IndexerDeclarationSyntax i => i.ExpressionBody?.Expression,
        _ => null
    };

    /// <summary>
    /// Every member an interface publishes to a consumer, its own and its base interfaces'.
    /// </summary>
    /// <remarks>
    /// <c>GetMembers()</c> returns only what a type declares itself, so
    /// <c>IOrderService : ICrudService&lt;Order&gt;</c> appears to declare nothing at all. Reading
    /// only the declared members would demote it while <c>ICrudService&lt;Order&gt;</c> hands its
    /// consumers a full set of writes. The base interfaces come from <see cref="INamedTypeSymbol.AllInterfaces"/>
    /// on the CONSTRUCTED interface, so type arguments are already substituted and each member can be
    /// handed to <c>FindImplementationForInterfaceMember</c> as-is.
    /// </remarks>
    private static IEnumerable<ISymbol> PublishedMembers(INamedTypeSymbol iface)
    {
        foreach (var member in iface.GetMembers()) yield return member;
        foreach (var baseInterface in iface.AllInterfaces)
            foreach (var member in baseInterface.GetMembers())
                yield return member;
    }

    /// <summary>
    /// Identity of a type for every lookup map in the scoring passes: <c>declaringAssembly|fullyQualifiedName</c>.
    /// The fully qualified name alone is NOT unique across a solution — two assemblies may each
    /// declare an internal <c>Shared.IOrderService</c> — and collapsing them would resolve a consumer
    /// to the wrong section, producing false cross-section findings or suppressing real ones. Keying
    /// on the <b>declaring</b> assembly is what makes a cross-assembly lookup still land correctly:
    /// a consumer in A injecting a type declared in B resolves through B's key, because the symbol
    /// it holds is B's.
    /// </summary>
    public static string TypeKey(ISymbol type) =>
        $"{type.ContainingAssembly?.Name}|{type.ToDisplayString()}";

    /// <summary>
    /// Every type declared under <paramref name="ns"/>, nested types included, regardless of
    /// accessibility. Exposed because a type the classifier drops as effectively private can still
    /// constrain a public one — a private <c>Derived : Base, IFoo</c> pins <c>Base.M</c>.
    /// </summary>
    public static IEnumerable<INamedTypeSymbol> EnumerateAllTypes(INamespaceSymbol ns) => EnumerateTypes(ns);

    private static IEnumerable<INamedTypeSymbol> EnumerateTypes(INamespaceSymbol ns)
    {
        foreach (var m in ns.GetMembers())
        {
            switch (m)
            {
                case INamespaceSymbol child:
                    foreach (var t in EnumerateTypes(child)) yield return t;
                    break;
                case INamedTypeSymbol type:
                    foreach (var t in WithNested(type)) yield return t;
                    break;
            }
        }
    }

    /// <summary>
    /// Whether a type is private once its containers are accounted for: itself <c>private</c>, or nested
    /// at any depth inside something that is.
    /// </summary>
    private static bool IsEffectivelyPrivate(INamedTypeSymbol type)
    {
        for (INamedTypeSymbol? t = type; t is not null; t = t.ContainingType)
            if (t.DeclaredAccessibility == Accessibility.Private) return true;
        return false;
    }

    /// <summary>
    /// A type and every type nested inside it, to any depth. Recursive rather than one level: an
    /// implementation nested inside a nested factory is still a type in this solution, and stopping at
    /// depth 1 made it invisible to every pass at once.
    /// </summary>
    private static IEnumerable<INamedTypeSymbol> WithNested(INamedTypeSymbol type)
    {
        yield return type;
        foreach (var nested in type.GetTypeMembers())
            foreach (var t in WithNested(nested))
                yield return t;
    }

    private static HashSet<string> Classify(SurfaceScoreConfig config, INamedTypeSymbol type, string filePath, string namespaceName)
    {
        var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, rule) in config.Classifications)
            if (Matches(rule, type, filePath, namespaceName))
                tags.Add(name);

        if (type.TypeKind == TypeKind.Interface)
        {
            tags.Remove("repositoryImplementation");
            tags.Remove("applicationService");
            tags.Remove("controller");
            tags.Remove("backgroundJob");
            if (tags.Contains("readServiceInterface")) tags.Remove("fullServiceInterface");
            if (tags.Contains("repositoryInterface")) tags.Remove("fullServiceInterface");
        }
        else
        {
            tags.Remove("readServiceInterface");
            tags.Remove("fullServiceInterface");
            tags.Remove("repositoryInterface");
            if (tags.Contains("repositoryImplementation")) tags.Remove("applicationService");
        }
        return tags;
    }

    private static bool Matches(ClassificationRule rule, INamedTypeSymbol type, string filePath, string namespaceName)
    {
        foreach (var p in rule.NamePatterns)
            if (GlobMatcher.MatchesName(type.Name, p)) return true;
        foreach (var p in rule.Paths)
            if (GlobMatcher.MatchesPath(filePath, p)) return true;
        foreach (var n in rule.Namespaces)
            if (namespaceName.StartsWith(n, StringComparison.Ordinal)) return true;
        foreach (var i in rule.Inherits)
            if (InheritsByName(type, i)) return true;
        foreach (var a in rule.AttributeNames)
            if (type.GetAttributes().Any(at => at.AttributeClass?.Name == a || at.AttributeClass?.Name == a + "Attribute")) return true;
        return false;
    }

    private static bool InheritsByName(INamedTypeSymbol type, string name)
    {
        var current = type.BaseType;
        while (current is not null)
        {
            if (current.Name == name) return true;
            current = current.BaseType;
        }
        foreach (var iface in type.AllInterfaces)
            if (iface.Name == name) return true;
        return false;
    }
}

/// <summary>
/// Maps assembly names to section names. Two rules, both purely structural:
/// <list type="number">
///   <item><c>&lt;X&gt;.Contracts</c> folds into <c>&lt;X&gt;</c> — a contracts assembly is the
///         published face of its section, not a section of its own.</item>
///   <item>The dot-segment prefix shared by every assembly in the solution is stripped for display,
///         so <c>Humans.Store</c> reports as <c>Store</c>. When stripping would leave nothing (the
///         monolith assembly that IS the prefix), the last segment is kept.</item>
/// </list>
/// </summary>
public static class AssemblySections
{
    private const string ContractsSuffix = ".Contracts";

    /// <summary>
    /// Assembly name -> section name, for the whole analyzed assembly set at once (the shared
    /// prefix is a property of the set, not of one name). Pass only assemblies that declare
    /// types — an empty docs/tooling project would otherwise erase the shared prefix.
    /// </summary>
    public static Dictionary<string, string> Resolve(IEnumerable<string> assemblyNames)
    {
        var folded = assemblyNames
            .Where(n => !string.IsNullOrEmpty(n))
            .Distinct(StringComparer.Ordinal)
            .ToDictionary(n => n, Fold, StringComparer.Ordinal);
        var segmented = folded.Values.Distinct(StringComparer.Ordinal).Select(v => v.Split('.')).ToList();

        int shared = segmented.Count == 0 ? 0 : segmented[0].Length;
        foreach (var segments in segmented.Skip(1))
            shared = Math.Min(shared, CommonLeadingSegments(segmented[0], segments));

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, foldedName) in folded)
        {
            var segments = foldedName.Split('.');
            result[name] = shared < segments.Length
                ? string.Join('.', segments.Skip(shared))
                : segments[^1];
        }

        // Stripping the shared prefix can land two DIFFERENT assemblies on one section name —
        // e.g. `Company.Product` (shared consumes it entirely, so it falls back to its last
        // segment) and `Company.Product.Product` (strips to its tail) both yield `Product`.
        // The grouping dictionaries downstream are case-insensitive, so that would silently
        // merge two unrelated assemblies into one section and pool their scores. Keyed on the
        // FOLDED name, so the intended `X` + `X.Contracts` collapse is left alone.
        var collided = result
            .GroupBy(pair => pair.Value, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Select(pair => folded[pair.Key]).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
            .SelectMany(g => g.Select(pair => pair.Key))
            .ToList();
        foreach (var name in collided)
            result[name] = folded[name];

        return result;
    }

    /// <summary>
    /// Whether the assembly is a section's published-contracts assembly (<c>&lt;X&gt;.Contracts</c>) —
    /// the one place a section's exported read API can live outside its own assembly.
    /// </summary>
    public static bool IsContractsAssembly(string assemblyName) =>
        assemblyName.EndsWith(ContractsSuffix, StringComparison.OrdinalIgnoreCase)
        && assemblyName.Length > ContractsSuffix.Length;

    private static string Fold(string assemblyName) =>
        IsContractsAssembly(assemblyName) ? assemblyName[..^ContractsSuffix.Length] : assemblyName;

    private static int CommonLeadingSegments(string[] a, string[] b)
    {
        int i = 0;
        while (i < a.Length && i < b.Length && string.Equals(a[i], b[i], StringComparison.OrdinalIgnoreCase)) i++;
        return i;
    }
}

/// <summary>
/// Effective accessibility: whether a declaration crosses its own assembly boundary, i.e.
/// whether another section can call it. A section is an assembly, so "public surface" is not a
/// judgement call — it is what the assembly exports.
/// </summary>
/// <remarks>
/// A declaration is exported only if it is <c>public</c> <b>and</b> every type containing it is,
/// walked out to the outermost declaration: a <c>public</c> method on an <c>internal</c> class is
/// internal, and so is a <c>public</c> type nested in one. Two deliberate exclusions:
/// <list type="bullet">
///   <item><c>protected</c> is not treated as exported. It is reachable only by deriving, and the
///         scoring passes have always required <see cref="Accessibility.Public"/> on members;
///         admitting protected types while still skipping protected members would be incoherent.</item>
///   <item><c>InternalsVisibleTo</c> is ignored. A test project or analyzer seeing internals does
///         not make them product surface — nothing ships against them.</item>
/// </list>
/// This gates the rules that charge for a <b>declaration's published shape</b>. Rules that charge
/// for a <b>use</b> — cross-section dependencies, duplicate DbSet ownership, DI registration — are
/// deliberately NOT gated: marking a consumer <c>internal</c> does not remove the assembly
/// reference, and the call still crosses the boundary. Gating those would have made coupling free.
/// </remarks>
public static class SurfaceVisibility
{
    public static bool IsExported(ISymbol symbol)
    {
        for (ISymbol? s = symbol; s is not null; s = s.ContainingType)
            if (s.DeclaredAccessibility != Accessibility.Public) return false;
        return true;
    }
}

/// <summary>A source type with its resolved section group, role tags, and primary location.</summary>
public sealed record ClassifiedType(
    INamedTypeSymbol Type,
    string Group,
    HashSet<string> Tags,
    string File,
    Location PrimaryLocation)
{
    public int Line => PrimaryLocation.GetLineSpan().StartLinePosition.Line + 1;

    /// <summary>
    /// Whether this type is visible outside its declaring assembly — see <see cref="SurfaceVisibility"/>.
    /// Internal types stay in the corpus (their implementation still counts toward the
    /// internal-complexity axis) but score nothing on the surface axis.
    /// </summary>
    public bool IsExported => SurfaceVisibility.IsExported(Type);
}
