namespace SampleSolution.Core.Models;

/// <summary>
/// Another BaseEntity derivative. Used to test `inheritors` for BaseEntity.
/// </summary>
public class AuditLog : BaseEntity
{
    public string Action { get; set; } = string.Empty;
    public string EntityName { get; set; } = string.Empty;
    public int EntityId { get; set; }
    public string PerformedBy { get; set; } = string.Empty;
}
