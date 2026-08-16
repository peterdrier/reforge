// Satellite of crossSectionReadInterface.CheapestFix.cs. Camp still publishes the same read
// interface it published in the Before — the cheapest fix does not delete anything over there, it
// just stops calling it, which is the point: the duplicate is additive.
//
// Held identical in shape to the Before's satellite (one exported single-method read interface) so
// its own charges cancel out of the pair's delta. The name differs only because both variants'
// files share one assembly in the full sample solution.

namespace SampleSolution.Gate.Rules;

public interface ICampAttendanceServiceRead
{
    int CountActive(int campId);
}
