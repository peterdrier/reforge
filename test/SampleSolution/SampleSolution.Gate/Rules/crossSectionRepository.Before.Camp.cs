// Satellite of crossSectionRepository.Before.cs — the ".Camp" segment compiles this into assembly
// SampleSolution.Camp, which is what puts the repository in a different section from its consumer.
//
// A repository makes Camp repo-backed, so the missing* rules charge Camp in both variants. The rule
// under test requires a repository, so there is no version of this pair without that, and the charge
// is constant across the two.

namespace SampleSolution.Gate.Rules;

public interface ICampLedgerBeforeRepository
{
    Task<string> LoadAsync(int campId, CancellationToken ct);
}
