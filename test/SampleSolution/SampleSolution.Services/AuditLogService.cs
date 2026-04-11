using SampleSolution.Core.Models;
using SampleSolution.Services.Data;

namespace SampleSolution.Services;

/// <summary>
/// Tests audit-immutable command — has both allowed and violating patterns for append-only entities.
/// </summary>
public class AuditLogService
{
    private readonly AppDbContext _dbContext;

    public AuditLogService(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    // OK — append-only, just adding
    public void LogAction(string action)
    {
        _dbContext.AuditLogs.Add(new AuditLog { Action = action });
    }

    // VIOLATION — removing from append-only
    public void DeleteOldLogs()
    {
        var old = _dbContext.AuditLogs.First();
        _dbContext.AuditLogs.Remove(old);
    }

    // VIOLATION — mutating append-only entity
    public void ArchiveLog(AuditLog log)
    {
        log.Action = "ARCHIVED";
    }
}
