// gate1: crossSectionReadInterface
//
// Gate injects Camp's read interface for one calculation. The cheapest fix stops injecting it and
// copies the calculation into a private helper: the dependency is gone and the rule goes to zero.
//
// This is the trade the rule should be most afraid of. crossSectionReadInterface is priced at 2,
// the cheapest charge in the whole config, precisely because reaching for another section's *read*
// API is the good version of a cross-section dependency: it is narrow, it is published, and it goes
// through the owning section. Duplicating the logic instead is the bad version — Camp's rule for who
// counts as active now lives in two places, and the second copy will not be updated with the first.
//
// What stops the trade paying is not a charge for the duplicate (nothing prices a private helper)
// but the orphan it leaves behind: Camp still publishes the read method and nothing names it any
// more, so exportedUnusedExternally charges Camp the 2 points Gate just saved. The total holds. A
// weight increase on crossSectionReadInterface would reopen the gap, because the more the dependency
// costs, the more the copy pays.

namespace SampleSolution.Gate.Rules;

public sealed class GateHeadcountBeforeService
{
    private readonly ICampRosterServiceRead _roster;

    public GateHeadcountBeforeService(ICampRosterServiceRead roster) => _roster = roster;

    public int ActiveHeadcount(int campId) => _roster.CountActive(campId);
}
