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
