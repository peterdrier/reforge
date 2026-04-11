using SampleSolution.Core.Interfaces;
using SampleSolution.Core.Models;

namespace SampleSolution.Services.Data;

/// <summary>
/// Repository implementation. Part of the call chain:
/// UserService.GetUserAsync -> UserRepository.FindByIdAsync -> BaseRepository.ExecuteQueryAsync
/// </summary>
public class UserRepository : BaseRepository, IUserRepository
{
    public async Task<User?> FindByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        return await ExecuteQueryAsync<User>(
            $"SELECT * FROM Users WHERE Id = {id}",
            cancellationToken);
    }

    public async Task<IReadOnlyList<User>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var results = await ExecuteQueryListAsync<User>(
            "SELECT * FROM Users",
            cancellationToken);
        return results.AsReadOnly();
    }

    public async Task SaveAsync(User user, CancellationToken cancellationToken = default)
    {
        await ExecuteCommandAsync(
            $"INSERT INTO Users VALUES ({user.Id}, '{user.Name}')",
            cancellationToken);
    }
}
