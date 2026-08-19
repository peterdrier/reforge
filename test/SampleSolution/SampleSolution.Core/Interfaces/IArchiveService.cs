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
