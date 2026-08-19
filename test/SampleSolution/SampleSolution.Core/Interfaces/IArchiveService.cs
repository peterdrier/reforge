namespace SampleSolution.Core.Interfaces;

/// <summary>
/// Fixture for the inherited-members case in <c>DemoteReadOnlyServiceInterfaces</c>. This interface
/// declares nothing but a read, so reading only its OWN members would demote it — but every consumer
/// also gets <see cref="IArchiveWriter{T}.Persist"/>, whose implementation commits. It must stay
/// <c>fullServiceInterface</c>.
/// </summary>
public interface IArchiveService : IArchiveWriter<string>
{
    string GetArchivedName(int id);
}

/// <summary>
/// Generic on purpose: the base interface reaches the implementation as a CONSTRUCTED type, so the
/// member handed to <c>FindImplementationForInterfaceMember</c> has its type argument substituted.
/// </summary>
public interface IArchiveWriter<T>
{
    void Persist(T value);
}

/// <summary>
/// Fixture for the settable-property case. Every method here is read-shaped, so nothing in a body
/// argues for a write — but <see cref="RetentionDays"/> has a setter, which hands every consumer a
/// mutation the implementation cannot withdraw.
/// </summary>
public interface IRetentionService
{
    int RetentionDays { get; set; }

    string GetPolicyName();
}

/// <summary>
/// Fixture for the incomplete-observation case. Its only in-solution implementer leaves the member
/// abstract, so no body is ever observed. A bodyless data-returning declaration reads as a query
/// under the shape heuristic, which would demote on an absence — the classification must be
/// preserved instead.
/// </summary>
public interface ILedgerService
{
    string GetLedgerName(int id);
}

/// <summary>
/// Fixture for the getter-body case. Every member is read-shaped and the property has no setter, so
/// nothing on the declaration argues for a write — but the implementing getter commits.
/// </summary>
public interface IQuotaService
{
    int CurrentQuota { get; }
}

/// <summary>
/// Fixture for the private-implementer case. Its only implementer is a private nested class behind a
/// factory, which is not scored surface but is still implementation evidence: the interface IS
/// implemented in this solution, read-only, so it must demote.
/// </summary>
public interface ILookupService
{
    string GetLabel(int id);
}

/// <summary>
/// Fixture for the deep-nesting case. Its only implementer is nested two levels down, which the
/// one-level type enumeration could not see at all.
/// </summary>
public interface IBadgeService
{
    string GetBadge(int id);
}

/// <summary>
/// Fixture for the partial-method case. Its implementation is a public partial method whose defining
/// declaration carries no body, so reading only the first declaration reports a gap for a member that
/// is fully implemented and read-only.
/// </summary>
public interface IManifestService
{
    string GetManifest(int id);
}

/// <summary>
/// Fixture for the abstract-exemption path. Its declared implementer is an abstract class that leaves
/// the member abstract; the concrete derived class fills it in, read-only. Completeness is required of
/// every CONCRETE implementer, and holding the abstract base to that standard too would block
/// demotion for every interface that has one — so this must demote.
/// </summary>
public interface IRosterService
{
    string GetRosterName(int id);
}

/// <summary>
/// Fixture for the writable-ref-property case. No setter, and the implementation is
/// <c>=&gt; ref _current</c> — no persistence call — yet <c>svc.Current = 5</c> compiles and writes.
/// </summary>
public interface IGaugeService
{
    ref int Current { get; }
}

/// <summary>Fixture for the same rule reached through a method rather than a property.</summary>
public interface ISlotService
{
    ref int GetSlot(int index);
}

/// <summary>
/// Negative control for both. <c>ref readonly</c> hands out a reference that cannot be assigned
/// through, so this is a read surface and must demote — otherwise the rule above is just "any ref".
/// </summary>
public interface IReadingService
{
    ref readonly int Value { get; }

    ref readonly int GetReading(int index);
}

/// <summary>
/// Fixture for the mutable-interface-field case. Every method is read-shaped, but
/// <c>IStateService.Current = 5</c> writes straight through the static field — an interface may declare
/// one since C# 8.
/// </summary>
public interface IStateService
{
    public static int Current = 0;

    string GetState();
}

/// <summary>
/// Negative control for the field rule: <c>readonly</c> and <c>const</c> cannot be written, so this
/// must still demote.
/// </summary>
public interface ISettledService
{
    public const int Limit = 10;
    public static readonly int Ceiling = 20;

    string GetSetting();
}

/// <summary>
/// Fixture for the non-public-member case. C# 8 allows a private helper on an interface; no consumer
/// can call it, and it is command-shaped, so counting it would invent a write for a read-only API.
/// </summary>
public interface IDigestService
{
    string GetDigest(int id);

    private void ResetCounters() { }
}

/// <summary>
/// Fixture for the public STATIC command. <c>IPurgeService.ClearAll()</c> is callable with no instance
/// and no implementing type, so skipping every static member let a published command go unseen. A static
/// member with a body carries it on the interface, so this one is decided without an implementer at all.
/// Must stay <c>fullServiceInterface</c>.
/// </summary>
public interface IPurgeService
{
    string GetPurgeState(int id);

    /// <summary>Command-shaped: returns no data and is not named for a query, so the shape decides it.</summary>
    public static void ClearAll() { }
}

/// <summary>
/// Negative control for <see cref="IPurgeService"/>: a public static method that RETURNS data, so the
/// command shape does not apply. Without it, "any public static method is a write" would satisfy that
/// fixture just as well.
/// </summary>
public interface ITallyService
{
    string GetTallyState(int id);

    public static int Count() => 0;
}

/// <summary>
/// Fixture for the <c>static abstract</c> command. There is no body here to read, so unlike
/// <see cref="IPurgeService"/> this one is observed on the implementing type — the same path an
/// instance method takes.
/// </summary>
public interface IStampService
{
    static abstract void Stamp(int id);
}

/// <summary>
/// Negative control for <see cref="IStampService"/>: a <c>static abstract</c> member whose implementation
/// reads. It demotes only if the static abstract implementation is actually resolved and read — if that
/// lookup silently failed, the member would count as unobserved and the interface would stay full.
/// </summary>
public interface IPollService
{
    static abstract int Poll(int id);
}

/// <summary>
/// Fixture for setter accessibility. <c>Value</c> is publicly readable and its setter is <c>private</c>,
/// so no consumer can write through it — matching any non-null <c>SetMethod</c> read this as published
/// write capability. Every other member is read-shaped, so it must demote.
/// </summary>
public interface IVaultService
{
    int Value { get => 0; private set { } }

    string GetVaultName(int id);
}

/// <summary>
/// Fixture for the expression-bodied INDEXER case. An indexer is an <c>IPropertySymbol</c> like any
/// other, but its declaration syntax is <c>IndexerDeclarationSyntax</c>, which is not a
/// <c>PropertyDeclarationSyntax</c> — so reading only the latter's <c>ExpressionBody</c> made every
/// arrow-bodied indexer getter look bodyless, and a bodyless getter in source reads as an
/// auto-property. This one's getter commits, so the interface must stay <c>fullServiceInterface</c>.
/// </summary>
public interface IShelfService
{
    int this[int slot] { get; }
}

/// <summary>
/// Negative control for <see cref="IShelfService"/>: same shape, same arrow, but the getter commits
/// nothing. Without it, "any indexer is a gap" would pass the test above just as well.
/// </summary>
public interface IRackService
{
    int this[int slot] { get; }
}

/// <summary>
/// Fixture for the static-only interface. Its whole published surface is a static query whose body is
/// right here, so it is fully decided with no implementer — and it has none, deliberately. Waiting for an
/// implementation that will never exist kept a definitively read-only surface classified as a write.
/// </summary>
public interface IClockService
{
    public static int GetTicks() => 0;
}

/// <summary>
/// Fixture for the static GETTER. <c>Current</c> is callable as <c>IMeterService.Current</c> with no
/// instance, and its body commits — the sibling of <see cref="IPurgeService"/>'s static method, which was
/// scanned while static getters were not. Must stay <c>fullServiceInterface</c>.
/// </summary>
public interface IMeterService
{
    public static int Current => MeterBacking.Db.SaveChanges();

    string GetMeterLabel(int id);
}

/// <summary>
/// Negative control for <see cref="IMeterService"/>: same static getter shape, a body that reads. Without
/// it, "any static getter is a write" would satisfy that fixture just as well.
/// </summary>
public interface IDialService
{
    public static int Current => 7;

    string GetDialLabel(int id);
}

/// <summary>
/// Fixture for <c>static virtual</c>. The default body here reads, but an implementer can REPLACE it, and
/// a call through a constrained type parameter dispatches to the replacement — so unlike a plain static
/// member this one settles nothing on the declaration. <see cref="BeaconService"/>'s override commits, so
/// it must stay <c>fullServiceInterface</c>.
/// </summary>
public interface IBeaconService
{
    static virtual int Ping() => 0;
}

/// <summary>
/// Negative control for <see cref="IBeaconService"/>: same shape, an override that reads. Without it,
/// "any static virtual member is unknowable" would satisfy that fixture just as well.
/// </summary>
public interface IChimeService
{
    static virtual int Ping() => 0;
}

/// <summary>
/// Fixture for the field INITIALIZER. <c>Blown</c> cannot be assigned through — it is <c>readonly</c> — but
/// the first consumer access runs its initializer, which commits. Declaring every readonly field harmless
/// missed a write that needs no method and no implementer to reach. Must stay <c>fullServiceInterface</c>.
/// </summary>
public interface IFuseService
{
    public static readonly int Blown = MeterBacking.Db.SaveChanges();

    string GetFuseLabel(int id);
}
