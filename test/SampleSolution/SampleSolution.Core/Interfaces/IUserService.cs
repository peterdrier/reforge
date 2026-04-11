using SampleSolution.Core.Models;

namespace SampleSolution.Core.Interfaces;

/// <summary>
/// Primary interface for user operations. Has 2 implementations (UserService, CachedUserService)
/// to test `implementations` command.
/// </summary>
public interface IUserService
{
    Task<User?> GetUserAsync(int id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<User>> GetAllUsersAsync(CancellationToken cancellationToken = default);
}
