using SampleSolution.Core.Models;

namespace SampleSolution.Web.Controllers;

/// <summary>
/// Intentional design rule violations for Phase 3 audit testing:
/// 1. Directly uses AppDbContext (DbContext-like class) instead of going through a service
/// 2. Has a method with bool isAdmin parameter (privileged boolean)
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

/// <summary>
/// Minimal DbContext-like class for testing design rule violations.
/// Not a real EF Core DbContext -- just enough to simulate the pattern.
/// </summary>
public class AppDbContext
{
    public List<User> Users { get; set; } = [];
    public List<AuditLog> AuditLogs { get; set; } = [];
}
