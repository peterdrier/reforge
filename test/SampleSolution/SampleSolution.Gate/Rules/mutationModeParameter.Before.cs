// gate1: mutationModeParameter
//
// A public mutation whose behaviour is selected by an action enum, with the branching inline — no
// delegation, so the structural dispatcher rule does not see it. This is the shape the rule was
// added for: the fold is entirely in the signature, and `ProcessNotice(id, mode)` admits three
// distinct operations without naming any of them.

namespace SampleSolution.Gate.Rules;

public sealed class GateNoticeBeforeService
{
    private int _sent;
    private int _held;
    private int _dropped;

    public void ProcessNotice(int noticeId, GateNoticeMode mode)
    {
        if (mode == GateNoticeMode.Send) _sent += noticeId > 0 ? 1 : 0;
        else if (mode == GateNoticeMode.Hold) _held++;
        else _dropped++;
    }

    public int SentCount() => _sent;
}

public enum GateNoticeMode
{
    Send,
    Hold,
    Drop,
}
