namespace SampleSolution.Core.Data;

/// <summary>
/// Stub for testing — mimics EF Core's DbSet.
/// </summary>
public class DbSet<T> : List<T> where T : class
{
    public void RemoveRange(IEnumerable<T> entities) { }
    public void Update(T entity) { }
    public new void AddRange(IEnumerable<T> entities) { }
    public void AddAsync(T entity) { }
    public DbSet<T> Where(Func<T, bool> predicate) => this;
    public T First() => this[0];
    public T First(Func<T, bool> predicate) => this[0];
    public T? FirstOrDefault(Func<T, bool> predicate) => default;
    public int ExecuteUpdate(Func<T, T> updateExpression) => 0;
    public int ExecuteDelete() => 0;
    public int ExecuteDeleteAsync() => 0;
}
