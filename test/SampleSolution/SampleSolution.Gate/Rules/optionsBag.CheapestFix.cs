// gate1: optionsBag
//
// The bag is gone; its four fields are now four parameters. Every caller passes the same four
// values it passed before, in a fixed order, with nothing named at the callsite — which is the
// situation parameter objects exist to get out of. optionsBag stops firing and
// methodParameterOverflow picks up two points for the two parameters past the allowance, so the
// agent trades a heavy charge for a light one and the total falls.
//
// That is a weighting failure rather than a detection failure, and it points both ways: the rules
// disagree about which of these two shapes is worse, and an agent will always move toward whichever
// one is cheaper. Two parameters over the line and one options bag describe roughly the same
// problem — too much crossing a boundary at once — so charging 8 for one and 2 for the other is an
// instruction to unbundle.
//
// Note also what optionsBag cannot see: the reason to dislike a bag is that its fields are
// independently optional and nothing validates their combinations. Counting properties finds that
// only by proxy, which is why the rule cannot tell this fix from a real one.

namespace SampleSolution.Gate.Rules;

public sealed class GateSyncCheapestFixService
{
    public void Configure(string endpoint, int retryCount, int timeoutSeconds, string mode)
    {
        _ = (endpoint, retryCount, timeoutSeconds, mode);
    }
}
