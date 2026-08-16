// Satellite of crossSectionFullService.Before.cs — the ".Camp" segment compiles this into assembly
// SampleSolution.Camp when the variant is scored alone, which is what puts the consumer and the
// dependency in different sections. Identical to the CheapestFix's satellite on purpose: the
// published interface does not change between the two variants, only where Gate injects it, so the
// points the pair moves are the pair's own.
//
// No repository type here, deliberately. A *Repository would make the section repo-backed and turn
// the missing* rules on, which would add points to both variants and drown the 8 the pair is about.

namespace SampleSolution.Gate.Rules;

public interface ICampBillingService
{
    int BalanceFor(int campId);
}
