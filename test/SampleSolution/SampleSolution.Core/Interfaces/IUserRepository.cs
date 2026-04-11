using SampleSolution.Core.Models;

namespace SampleSolution.Core.Interfaces;

public interface IUserRepository
{
    Task<User?> FindByIdAsync(int id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<User>> GetAllAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(User user, CancellationToken cancellationToken = default);
}
