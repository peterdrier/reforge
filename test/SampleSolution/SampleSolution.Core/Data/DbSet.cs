namespace SampleSolution.Core.Data;

/// <summary>
/// Stub for testing — mimics EF Core's DbSet.
/// </summary>
public class DbSet<T> : List<T> where T : class
{
}
