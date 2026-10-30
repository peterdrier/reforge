// gate1: mutationModeParameter
//
// The rule recognises a mode selector by its *type* — an enum. So the cheapest way out is to stop
// using an enum. `string mode` carries the identical choice to the identical call sites and selects
// the identical three behaviours.
//
// This is a downgrade in every direction that matters. The compiler no longer knows the set of
// legal values, no longer rejects a misspelling, and "urgnet" now silently sends at normal weight.

namespace SampleSolution.Gate.Rules;

public sealed class GateNoticeCheapestFixService
{
    private int _sent;

    public void ProcessNotice(int noticeId, string mode)
    {
        int weight = 1;
        if (mode == "Urgent") weight = 3;
        else if (mode == "Quiet") weight = 0;
        _sent += noticeId > 0 ? weight : 0;
    }

    public int SentCount() => _sent;
}
