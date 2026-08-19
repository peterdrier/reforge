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
