using SampleSolution.Core.Models;
using SampleSolution.Services.Data;

namespace SampleSolution.Web.Controllers;

/// <summary>
/// Intentional design rule violations for Phase 3 audit testing:
/// 1. Directly uses AppDbContext (DbContext-like class) instead of going through a service
/// 2. Has a method with bool isAdmin parameter (privileged boolean)
/// Also tests ownership-violations: controller accesses Users and AuditLogs tables directly.
/// </summary>
public class BadController
{
    private readonly AppDbContext _dbContext;

    public BadController(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    /// <summary>
    /// Design violation: controller directly uses DbContext.
    /// </summary>
    public User? GetUserDirectly(int id)
    {
        return _dbContext.Users.FirstOrDefault(u => u.Id == id);
    }

    /// <summary>
    /// Design violation: bool isAdmin parameter (privileged boolean anti-pattern).
    /// </summary>
    public IReadOnlyList<User> GetUsers(bool isAdmin, int maxResults = 100)
    {
        if (isAdmin)
        {
            return _dbContext.Users.Take(maxResults).ToList().AsReadOnly();
        }

        return _dbContext.Users.Where(u => u.IsActive).Take(maxResults).ToList().AsReadOnly();
    }
}
