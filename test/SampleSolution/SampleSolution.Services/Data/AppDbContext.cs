using SampleSolution.Core.Data;
using SampleSolution.Core.Models;

namespace SampleSolution.Services.Data;

/// <summary>
/// Application DbContext for testing dbset-usage, ownership-violations, and service-map commands.
/// </summary>
public class AppDbContext : DbContext
{
    public DbSet<User> Users { get; set; } = new();
    public DbSet<AuditLog> AuditLogs { get; set; } = new();
}
