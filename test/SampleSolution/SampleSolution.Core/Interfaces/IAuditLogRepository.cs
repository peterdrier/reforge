namespace SampleSolution.Core.Interfaces;

/// <summary>
/// Repository fixture for audit-downstream issue #8 — methods exercise DbSet access in the
/// repo body (so callers should surface DbSet usage with `via` attribution) and a repo-to-repo
/// hop (so callers should surface untracedRepoCalls).
/// </summary>
public interface IAuditLogRepository
{
    Task<int> CountAsync(CancellationToken cancellationToken = default);
    Task PurgeAsync(CancellationToken cancellationToken = default);
    Task<int> CountUsersAsync(CancellationToken cancellationToken = default);
}
