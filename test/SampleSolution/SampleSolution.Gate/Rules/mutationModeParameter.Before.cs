// gate1: mutationModeParameter
// gate1-gameable: the enum selector becomes `string mode`. Same choice, same call sites, same three
// behaviours — the selector just loses its type, so the rule (which recognises a mode parameter by
// its enum-ness) stops seeing it. 35 -> 10, and nothing else charges anything.
//
// This is the worst of the three tranche-3 results, because the fix is a strict downgrade with no
// backstop at all. After it the compiler no longer knows the legal set, no longer rejects a
// misspelling, no longer reports the switch non-exhaustive, and the third arm silently swallows
// "hlod" as a drop. Every property the enum was carrying is gone and the score improved by 25.
//
// The gap is that Reforge has no rule for a stringly-typed selector. mutationModeParameter tests
// for `TypeKind.Enum` and a name/suffix match, which is precise about the shape it was written for
// and blind to the shape you get by taking the type away. A rule that charges a mutation for
// branching on a string parameter — the same points, on the same argument that "there is a mode
// selector here" — would leave this fix costing what it costs today.
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
