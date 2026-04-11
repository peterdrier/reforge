namespace SampleSolution.Core.Interfaces;

/// <summary>
/// Stub for testing audit-cache command — mimics IMemoryCache.
/// </summary>
public interface IMemoryCache
{
    void Remove(string key);
    void Set(string key, object value);
}
