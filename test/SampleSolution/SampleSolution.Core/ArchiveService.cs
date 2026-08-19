using SampleSolution.Core.Interfaces;

namespace SampleSolution.Core;

/// <summary>Concrete implementation for the inherited-write-members fixture.</summary>
public class ArchiveService : IArchiveService
{
    private readonly List<string> _archived = new();

    public string GetArchivedName(int id) => _archived[id];

    public void Persist(string value) => _archived.Add(value);
}

/// <summary>Concrete implementation for the settable-property fixture. Nothing here writes.</summary>
public class RetentionService : IRetentionService
{
    public int RetentionDays { get; set; } = 30;

    public string GetPolicyName() => $"retain-{RetentionDays}";
}

/// <summary>
/// The only in-solution implementer of <see cref="ILedgerService"/>, and it implements nothing: the
/// member stays abstract, so the classifier observes no behavior at all.
/// </summary>
public abstract class LedgerServiceBase : ILedgerService
{
    public abstract string GetLedgerName(int id);
}

/// <summary>
/// Implements <see cref="IQuotaService"/> with a getter that commits. Pathological on purpose: the
/// point is that only the body says so.
/// </summary>
public class QuotaService : IQuotaService
{
    private readonly AuditDbContext _db = new();

    public int CurrentQuota
    {
        get
        {
            _db.SaveChanges();
            return 7;
        }
    }
}

/// <summary>Minimal stub so the getter above has something to commit on.</summary>
public class AuditDbContext
{
    public int SaveChanges() => 0;
}

/// <summary>
/// Publishes <see cref="ILookupService"/> without publishing a type. The implementation is private
/// and nested, so it never reaches the scored corpus.
/// </summary>
public static class LookupFactory
{
    public static ILookupService Create() => new Impl();

    private sealed class Impl : ILookupService
    {
        public string GetLabel(int id) => id.ToString();
    }
}

/// <summary>
/// Holds the <see cref="IBadgeService"/> implementation two levels deep. `EnumerateTypes` used to
/// yield a top-level type and its immediate children only, so `Inner.Impl` was invisible.
/// </summary>
public static class BadgeHost
{
    public static IBadgeService Create() => new Inner.Impl();

    public static class Inner
    {
        public sealed class Impl : IBadgeService
        {
            public string GetBadge(int id) => $"badge-{id}";
        }
    }
}
