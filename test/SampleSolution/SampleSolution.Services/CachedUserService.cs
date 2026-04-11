using SampleSolution.Core.Attributes;
using SampleSolution.Core.Interfaces;
using SampleSolution.Core.Models;

namespace SampleSolution.Services;

/// <summary>
/// Decorator implementation of IUserService that adds caching.
/// Tests: implementations (second impl of IUserService), injected (takes IUserService + ILogger),
///        attribute references (ServiceLifetime attribute).
/// </summary>
[ServiceLifetime("Singleton")]
public class CachedUserService : IUserService
{
    private readonly IUserService _inner;
    private readonly ILogger _logger;
    private readonly Dictionary<int, User> _cache = new();

    public CachedUserService(IUserService inner, ILogger logger)
    {
        _inner = inner;
        _logger = logger;
    }

    public async Task<User?> GetUserAsync(int id, CancellationToken cancellationToken = default)
    {
        if (_cache.TryGetValue(id, out var cached))
        {
            _logger.LogInfo($"Cache hit for user {id}");
            return cached;
        }

        var user = await _inner.GetUserAsync(id, cancellationToken);
        if (user != null)
        {
            _cache[id] = user;
        }
        return user;
    }

    public async Task<IReadOnlyList<User>> GetAllUsersAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInfo("Cache bypass: getting all users");
        return await _inner.GetAllUsersAsync(cancellationToken);
    }
}
