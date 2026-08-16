// gate1: crossSectionReadInterface
// gate1-gameable: Gate stops injecting Camp's read interface and copies the one calculation it
// wanted into itself. The dependency is gone, the rule goes to zero, and the duplicate costs
// nothing — a private method is not surface, so nothing in the model charges for it.
//
// This is the trade the rule should be most afraid of. crossSectionReadInterface is priced at 2,
// the cheapest charge in the whole config, precisely because reaching for another section's *read*
// API is the good version of a cross-section dependency: it is narrow, it is published, and it goes
// through the owning section. Duplicating the logic instead is the bad version — Camp's rule for who
// counts as active now lives in two places, and the second copy will not be updated with the first.
//
// So the rule is directionally right and still gameable, which is the awkward case: an agent that
// clears every charge it can, in weight order, arrives at this one last and pays nothing to remove
// the cheapest and most defensible dependency in the codebase.
//
// Closing it needs something that charges for duplication, which nothing here does. A weight
// increase would make it worse, not better: the more crossSectionReadInterface costs, the more the
// duplicate pays.

namespace SampleSolution.Gate.Rules;

public sealed class GateHeadcountBeforeService
{
    private readonly ICampRosterServiceRead _roster;

    public GateHeadcountBeforeService(ICampRosterServiceRead roster) => _roster = roster;

    public int ActiveHeadcount(int campId) => _roster.CountActive(campId);
}
