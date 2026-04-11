namespace SampleSolution.Core.Models;

/// <summary>
/// Domain model User. Shares the simple name "User" with Dto.User to test ambiguous symbol resolution.
/// </summary>
public class User : BaseEntity
{
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
}
