// gate1: mutationModeParameter
//
// The rule recognises a mode selector by its *type* — an enum. So the cheapest way out is to stop
// using an enum. `string mode` carries the identical choice to the identical call sites and selects
// the identical three behaviours.
//
// This is a downgrade in every direction that matters. The compiler no longer knows the set of
// legal values, no longer rejects a misspelling, no longer tells you where the switch is
// non-exhaustive, and the third arm now silently swallows "hlod" as a drop. The fold the rule
// objects to is not merely still present — it got worse, because the selector lost its type.

namespace SampleSolution.Gate.Rules;

public sealed class GateNoticeCheapestFixService
{
    private int _sent;
    private int _held;
    private int _dropped;

    public void ProcessNotice(int noticeId, string mode)
    {
        if (mode == "Send") _sent += noticeId > 0 ? 1 : 0;
        else if (mode == "Hold") _held++;
        else _dropped++;
    }

    public int SentCount() => _sent;
}
