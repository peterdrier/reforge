namespace SampleSolution.Core.Data;

/// <summary>
/// Stub for testing — mimics EF Core's DbContext base class.
/// </summary>
public abstract class DbContext
{
    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);
    public int SaveChanges() => 0;
}
