// gate1: projectReference
//
// Gate calls a static rule Camp publishes. No constructor dependency, so no crossSection* rule sees
// the edge; projectReference is what prices Gate needing Camp at all.
//
// The cheapest fix copies the rule into Gate and drops the reference. What keeps that from paying
// is the same balance crossSectionReadInterface relies on: the copy is a declaration of its own
// (methodCount), and Camp's rule, now named by no other assembly, pays exportedUnusedExternally.
// Together they are the floor's weight, so the trade breaks even at best.

namespace SampleSolution.Gate.Rules;

public sealed class GateFeeBeforeService
{
    public decimal FeeFor(int daysLate) => CampFeeRules.LateFee(daysLate);
}
