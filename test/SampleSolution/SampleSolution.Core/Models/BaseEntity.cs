namespace SampleSolution.Core.Models;

/// <summary>
/// Abstract base entity with common fields. Used to test `inheritors` command.
/// </summary>
public abstract class BaseEntity
{
    public int Id { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
