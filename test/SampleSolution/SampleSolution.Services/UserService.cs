using SampleSolution.Core.Attributes;
using SampleSolution.Core.Interfaces;
using SampleSolution.Core.Models;
using SampleSolution.Services.Data;

namespace SampleSolution.Services;

/// <summary>
/// Primary IUserService implementation.
/// Tests: implementations, injected (takes IUserRepository), dependencies,
///        members (variety of member types), callers/call-chain,
///        dbset-usage (accesses Users and AuditLogs through AppDbContext).
/// </summary>
[ServiceLifetime("Scoped")]
public class UserService : IUserService
{
    private readonly IUserRepository _userRepository;
    private readonly ILogger _logger;
    private readonly AppDbContext _dbContext;
    private static int _instanceCount;

    public bool IsInitialized { get; private set; }

    public UserService(IUserRepository userRepository, ILogger logger, AppDbContext dbContext)
    {
        _userRepository = userRepository;
        _logger = logger;
        _dbContext = dbContext;
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
}
