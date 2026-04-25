using SampleSolution.Core.Data;
using SampleSolution.Core.Interfaces;
using SampleSolution.Services.Data;

namespace SampleSolution.Services;

/// <summary>
/// Audit-downstream issue #8 fixtures:
///   - <c>GetCountAsync</c>: repo body access propagates with `via` attribution.
///   - <c>GetUserCountAsync</c>: repo-to-repo call surfaces as <c>untracedRepoCalls</c>.
///   - <c>CountWithFactoryAsync</c>: DbSet access through an <c>IDbContextFactory</c>-produced local.
/// </summary>
public class AuditLogQueryService
{
    private readonly IAuditLogRepository _repo;
    private readonly IDbContextFactory<AppDbContext> _factory;

    public AuditLogQueryService(IAuditLogRepository repo, IDbContextFactory<AppDbContext> factory)
    {
        _repo = repo;
        _factory = factory;
    }

    public async Task<int> GetCountAsync(CancellationToken cancellationToken = default) =>
        await _repo.CountAsync(cancellationToken);

    public async Task PurgeAsync(CancellationToken cancellationToken = default) =>
        await _repo.PurgeAsync(cancellationToken);

    public async Task<int> GetUserCountAsync(CancellationToken cancellationToken = default) =>
        await _repo.CountUsersAsync(cancellationToken);

    public async Task<int> CountWithFactoryAsync(CancellationToken cancellationToken = default)
    {
        var db = await _factory.CreateDbContextAsync(cancellationToken);
        return db.AuditLogs.Count();
    }
}
