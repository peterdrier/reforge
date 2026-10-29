namespace SampleSolution.Services;

/// <summary>
/// Fixture for the write verdict following calls: <see cref="TryRenameAsync"/> returns data and
/// commits nothing in its own body, so on shape alone it reads as a query. It reaches
/// <c>SaveChangesAsync</c> through <see cref="UserService.UpdateUserNameAsync"/>, which is what
/// makes it a write. <see cref="CountRenames"/> is the control: same type, no path to a commit.
/// </summary>
public sealed class UserRenameService
{
    private readonly UserService _users;

    public UserRenameService(UserService users) => _users = users;

    public async Task<bool> TryRenameAsync(int id, string name, CancellationToken cancellationToken = default)
    {
        await _users.UpdateUserNameAsync(id, name, cancellationToken);
        return true;
    }

    public int CountRenames() => 0;
}
