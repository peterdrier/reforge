// The cheapest fix for missingReadSurface: a correctly named interface with nothing in it.

namespace SampleSolution.Gate.Rules;

public interface IGateReadableFixRepository
{
    Task<string> LoadAsync(int id, CancellationToken ct);
}

public interface IGateReadableFixService
{
    void Save(int id);
}

// The whole edit. Matches the readServiceInterface pattern, empty, unimplemented.
public interface IGateReadableFixServiceRead
{
}
