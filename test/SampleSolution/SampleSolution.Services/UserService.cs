using SampleSolution.Core.Attributes;
using SampleSolution.Core.Interfaces;
using SampleSolution.Core.Models;

namespace SampleSolution.Services;

/// <summary>
/// Primary IUserService implementation.
/// Tests: implementations, injected (takes IUserRepository), dependencies,
///        members (variety of member types), callers/call-chain.
/// </summary>
[ServiceLifetime("Scoped")]
public class UserService : IUserService
{
    private readonly IUserRepository _userRepository;
    private readonly ILogger _logger;
    private static int _instanceCount;

    public bool IsInitialized { get; private set; }

    public UserService(IUserRepository userRepository, ILogger logger)
    {
        _userRepository = userRepository;
        _logger = logger;
        _instanceCount++;
    }

    public async Task<User?> GetUserAsync(int id, CancellationToken cancellationToken = default)
    {
        _logger.LogInfo($"Getting user {id}");
        var user = await _userRepository.FindByIdAsync(id, cancellationToken);
        if (user == null)
        {
            _logger.LogWarning($"User {id} not found");
        }
        return user;
    }

    public async Task<IReadOnlyList<User>> GetAllUsersAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInfo("Getting all users");
        return await _userRepository.GetAllAsync(cancellationToken);
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
