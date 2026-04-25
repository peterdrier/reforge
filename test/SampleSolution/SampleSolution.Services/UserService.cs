using SampleSolution.Core.Attributes;
using SampleSolution.Core.Interfaces;
using SampleSolution.Core.Models;
using SampleSolution.Services.Data;

namespace SampleSolution.Services;

/// <summary>
/// Primary IUserService implementation.
/// Tests: implementations, injected (takes IUserRepository), dependencies,
///        members (variety of member types), callers/call-chain,
///        dbset-usage (accesses Users and AuditLogs through AppDbContext),
///        audit-cache (has both cache and DbContext — tests cache eviction detection).
/// </summary>
[ServiceLifetime("Scoped")]
public class UserService : IUserService
{
    private readonly IUserRepository _userRepository;
    private readonly ILogger _logger;
    private readonly AppDbContext _dbContext;
    private readonly IMemoryCache _cache;
    private static int _instanceCount;

    public bool IsInitialized { get; private set; }

    public UserService(IUserRepository userRepository, ILogger logger, AppDbContext dbContext, IMemoryCache cache)
    {
        _userRepository = userRepository;
        _logger = logger;
        _dbContext = dbContext;
        _cache = cache;
        _instanceCount++;
    }

    public async Task<User?> GetUserAsync(int id, CancellationToken cancellationToken = default)
    {
        _logger.LogInfo($"Getting user {id}");
        var user = _dbContext.Users.FirstOrDefault(u => u.Id == id);
        if (user == null)
        {
            _logger.LogWarning($"User {id} not found");
        }
        return await Task.FromResult(user);
    }

    public async Task<IReadOnlyList<User>> GetAllUsersAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInfo("Getting all users");
        return await Task.FromResult(_dbContext.Users.Where(u => u.IsActive).ToList().AsReadOnly());
    }

    /// <summary>
    /// Writes an audit log entry — tests dbset-usage finding AuditLogs access.
    /// </summary>
    public void LogAudit(string action, string entityName, int entityId, string performedBy)
    {
        _dbContext.AuditLogs.Add(new AuditLog
        {
            Action = action,
            EntityName = entityName,
            EntityId = entityId,
            PerformedBy = performedBy
        });
    }

    /// <summary>
    /// Protected method for testing member visibility in `members` command.
    /// </summary>
    protected virtual void OnUserLoaded(User user)
    {
        _logger.LogInfo($"User loaded: {user.Name}");
    }

    /// <summary>
    /// Static method for testing member variety in `members` command.
    /// </summary>
    public static int GetInstanceCount() => _instanceCount;

    /// <summary>
    /// Method with various parameter types for testing `parameters` command.
    /// </summary>
    public async Task<User?> FindUserByNameAsync(
        string name,
        bool exactMatch = false,
        int maxResults = 10,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInfo($"Finding user by name: {name}, exact={exactMatch}, max={maxResults}");
        await Task.Delay(1, cancellationToken);
        return null;
    }

    /// <summary>
    /// Method with bool isAdmin parameter -- design rule violation for Phase 3 audit.
    /// </summary>
    public async Task<User?> GetUserWithPrivilegeCheckAsync(
        int id,
        bool isAdmin,
        CancellationToken cancellationToken = default)
    {
        if (isAdmin)
        {
            _logger.LogInfo($"Admin access for user {id}");
        }
        return await _userRepository.FindByIdAsync(id, cancellationToken);
    }

    /// <summary>
    /// audit-cache violation: calls SaveChangesAsync without cache eviction.
    /// </summary>
    public async Task UpdateUserNameAsync(int id, string name, CancellationToken cancellationToken = default)
    {
        var user = _dbContext.Users.FirstOrDefault(u => u.Id == id);
        if (user != null)
        {
            user.Name = name;
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    /// <summary>
    /// audit-cache clean: calls SaveChangesAsync AND cache.Remove.
    /// </summary>
    public async Task UpdateUserEmailAsync(int id, string email, CancellationToken cancellationToken = default)
    {
        var user = _dbContext.Users.FirstOrDefault(u => u.Id == id);
        if (user != null)
        {
            user.Email = email;
            await _dbContext.SaveChangesAsync(cancellationToken);
            _cache.Remove($"user:{id}");
        }
    }

    /// <summary>
    /// audit-cache clean: calls SaveChangesAsync and delegates cache eviction to a helper.
    /// </summary>
    public async Task DeactivateUserAsync(int id, CancellationToken cancellationToken = default)
    {
        var user = _dbContext.Users.FirstOrDefault(u => u.Id == id);
        if (user != null)
        {
            user.IsActive = false;
            await _dbContext.SaveChangesAsync(cancellationToken);
            EvictUserCache(id);
        }
    }

    private void EvictUserCache(int id)
    {
        _cache.Remove($"user:{id}");
        _cache.Remove("user:all");
    }

    /// <summary>
    /// Tests audit-ef — string interpolation in LINQ predicate (violation).
    /// </summary>
    public object FindByInterpolation(string name)
    {
        return _dbContext.Users.Where(u => $"{u.Name}".Contains(name));
    }

    /// <summary>
    /// audit-surface passthrough-repo: single call on a repository field (issue #7).
    /// </summary>
    public async Task<User?> GetByRepoAsync(int id, CancellationToken cancellationToken = default) =>
        await _userRepository.FindByIdAsync(id, cancellationToken);

    /// <summary>
    /// audit-surface passthrough-self: single call on `this` via a private helper (issue #7).
    /// </summary>
    public void EvictUserCacheById(int id) => EvictUserCache(id);
}
