// Satellite of crossSectionRepository.CheapestFix.cs — same shape as the Before satellite under a
// different name, because both files compile into the sample solution alongside each other.

namespace SampleSolution.Gate.Rules;

public interface ICampLedgerFixRepository
{
    Task<string> LoadAsync(int campId, CancellationToken ct);
}
