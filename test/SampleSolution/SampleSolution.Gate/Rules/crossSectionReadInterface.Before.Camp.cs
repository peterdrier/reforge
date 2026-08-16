// Satellite of crossSectionReadInterface.Before.cs — compiled as SampleSolution.Camp when the
// variant is scored alone, so the consumer in Gate is reaching across a section boundary.
//
// Named I*ServiceRead so the default classification tags it readServiceInterface rather than
// fullServiceInterface; that is what selects crossSectionReadInterface (2) over
// crossSectionFullService (8) on the consuming side.

namespace SampleSolution.Gate.Rules;

public interface ICampRosterServiceRead
{
    int CountActive(int campId);
}
