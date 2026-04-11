namespace SampleSolution.Core.Dto;

/// <summary>
/// DTO version of User. Shares the simple name "User" with Models.User
/// to test ambiguous symbol resolution.
/// </summary>
public class User
{
    public int Id { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
}
