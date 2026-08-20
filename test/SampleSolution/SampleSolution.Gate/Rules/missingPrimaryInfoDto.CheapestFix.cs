// The cheapest fix for missingPrimaryInfoDto: a type of the right name that nothing returns.

namespace SampleSolution.Gate.Rules;

public interface IGateAnchorlessFixRepository
{
    Task<string> LoadAsync(int id, CancellationToken ct);
}

public interface IGateAnchorlessFixServiceRead
{
    Task<string> GetAsync(int id, CancellationToken ct);
}

public interface IGateAnchorlessFixService
{
    void Save(int id);
}

// The whole edit. GateInfo is the name the <Section>Info convention looks for, and this folder's
// only declaration of it — the other missing* fixtures let the rule fire in both their variants
// rather than compete for the name.
public sealed class GateInfo
{
    public int Id { get; set; }
}
