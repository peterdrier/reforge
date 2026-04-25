namespace SampleSolution.Core.Data;

/// <summary>
/// Stub mimicking EF Core's <c>IDbContextFactory&lt;TContext&gt;</c>. Used by audit-downstream
/// fixtures (issue #8) to verify DbSet access detection through factory-produced locals.
/// </summary>
public interface IDbContextFactory<TContext> where TContext : DbContext
{
    TContext CreateDbContext();
    Task<TContext> CreateDbContextAsync(CancellationToken cancellationToken = default);
}
