using SampleSolution.Core.Interfaces;

namespace SampleSolution.Services.Data;

/// <summary>
/// Concrete repository whose body directly accesses <c>AppDbContext.AuditLogs</c>.
/// Audit-downstream callers should propagate the DbSet read/write with `via`
/// attribution (issue #8). <c>CountUsersAsync</c> calls another repository so the
/// caller should surface that as <c>untracedRepoCalls</c>.
/// </summary>
public class AuditLogRepository : IAuditLogRepository
{
    private readonly AppDbContext _db;
    private readonly IUserRepository _userRepo;

    public AuditLogRepository(AppDbContext db, IUserRepository userRepo)
    {
        _db = db;
        _userRepo = userRepo;
    }

    public async Task<int> CountAsync(CancellationToken cancellationToken = default)
    {
        return await Task.FromResult(_db.AuditLogs.Count());
    }

    public async Task PurgeAsync(CancellationToken cancellationToken = default)
    {
        await Task.FromResult(_db.AuditLogs.ExecuteDeleteAsync());
    }

    public async Task<int> CountUsersAsync(CancellationToken cancellationToken = default)
    {
        var users = await _userRepo.GetAllAsync(cancellationToken);
        return users.Count;
    }
}
