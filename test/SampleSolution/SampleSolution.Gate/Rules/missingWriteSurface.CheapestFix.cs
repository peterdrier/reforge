// The cheapest fix for missingWriteSurface: name a write interface, implement nothing.

namespace SampleSolution.Gate.Rules;

public interface IGateWritableFixRepository
{
    Task<string> LoadAsync(int id, CancellationToken ct);
}

public interface IGateWritableFixServiceRead
{
    Task<string> GetAsync(int id, CancellationToken ct);
}

// The whole edit.
public interface IGateWritableFixService
{
    void Save(int id);
}
