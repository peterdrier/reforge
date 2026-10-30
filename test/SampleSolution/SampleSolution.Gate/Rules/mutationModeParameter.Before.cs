// gate1: mutationModeParameter
// gate1-gameable: the enum selector becomes `string mode`. Same choice, same call sites, same
// behaviour. The selector just loses its type, so the rule (which recognises a mode parameter by
// its enum-ness) stops seeing it, and nothing else charges anything.
//
// The fix is a strict downgrade with no backstop at all. After it the compiler no longer knows the
// legal set, no longer rejects a misspelling, and "urgnet" silently sends at normal weight. Every
// property the enum was carrying is gone and the score improved.
//
// The gap is that Reforge has no rule for a stringly-typed selector. mutationModeParameter tests
// for `TypeKind.Enum` and a name/suffix match, which is precise about the shape it was written for
// and blind to the shape you get by taking the type away. A rule that charges a mutation for
// branching on a string parameter — the same points, on the same argument that "there is a mode
// selector here" — would leave this fix costing what it costs today.
//
// A public mutation whose behaviour is steered by a mode enum, with the branching inline. The arms
// only pick a value the shared code then uses, so they are not operations of their own and
// actionDispatcher does not see them; the fold is entirely in the signature.

namespace SampleSolution.Gate.Rules;

public sealed class GateNoticeBeforeService
{
    private int _sent;

    public void ProcessNotice(int noticeId, GateNoticeMode mode)
    {
        int weight = 1;
        if (mode == GateNoticeMode.Urgent) weight = 3;
        else if (mode == GateNoticeMode.Quiet) weight = 0;
        _sent += noticeId > 0 ? weight : 0;
    }

    public int SentCount() => _sent;
}

public enum GateNoticeMode
{
    Normal,
    Urgent,
    Quiet,
}
