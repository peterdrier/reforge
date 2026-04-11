using SampleSolution.Core.Data;
using SampleSolution.Core.Models;

namespace SampleSolution.Services.Data;

/// <summary>
/// Application DbContext for testing dbset-usage, ownership-violations, service-map,
/// audit-immutable, and audit-ef commands.
/// </summary>
public class AppDbContext : DbContext
{
    public DbSet<User> Users { get; set; } = new();
    public DbSet<AuditLog> AuditLogs { get; set; } = new();

    /// <summary>
    /// Tests audit-ef command — contains sentinel trap, non-CLR default, and string conversion patterns.
    /// </summary>
    protected void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Sentinel trap — CLR default value (violation)
        modelBuilder.Entity<User>().Property(u => u.IsActive).HasDefaultValue(false);

        // OK — non-CLR default (not a violation)
        modelBuilder.Entity<User>().Property(u => u.Name).HasDefaultValue("Unknown");

        // String conversion warning (violation)
        modelBuilder.Entity<AuditLog>().Property(a => a.Action).HasConversion<string>();
    }
}
