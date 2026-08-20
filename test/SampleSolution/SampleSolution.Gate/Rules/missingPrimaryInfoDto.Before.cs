// gate1: missingPrimaryInfoDto
// Not marked gameable, and the reason is worth recording: the cheapest fix — a class named GateInfo
// that nothing returns — costs exactly the 10 the rule charges, so the total does not move. An agent
// watching the number gains nothing by naming a type, which is what keeps the rule honest even
// though nothing checks that the DTO is anchored to a read method.

namespace SampleSolution.Gate.Rules;

public interface IGateAnchorlessBeforeRepository
{
    Task<string> LoadAsync(int id, CancellationToken ct);
}

// Both in both variants: only the DTO differs.
public interface IGateAnchorlessBeforeServiceRead
{
    Task<string> GetAsync(int id, CancellationToken ct);
}

public interface IGateAnchorlessBeforeService
{
    void Save(int id);
}
