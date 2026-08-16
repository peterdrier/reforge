// gate1: actionDispatcher
// gate1-gameable: the three delegated members are deleted and their bodies pasted into the switch
// arms. Nothing routes to a distinct member any more, so the structural rule stops; the fold it
// objected to is untouched and three named units with their own signatures are gone. 62 -> 36.
//
// The interesting part is where the 26 points go. The cheapest fix does not escape scoring — it
// lands on mutationModeParameter, which charges 25 where actionDispatcher charged 41. So the
// backstop fires and still pays for the edit, because the rule for the *less* visible fold is
// cheaper than the rule for the more visible one.
//
// That ordering is backwards. Between two methods that hide the same three operations behind one
// enum, the one that delegates to `Open`/`Fill`/`Cancel` is the better of the two: the operations
// still have names, still have signatures, and can still be called directly by anything that knows
// which one it wants. Reforge prices it 41 and prices the inlined version 25, so an agent reading
// the score is told to inline.
//
// Closing this needs the two rules re-based against each other rather than a bigger number on
// either: while a structural dispatcher outprices an inline one, deleting the structure pays.
//
// A structural dispatcher: one public mutation switches on an action enum and routes each arm to a
// distinct private member. Three separate operations, one door, and the door's name (Apply) says
// nothing about which of the three runs.
//
// The rule charges for the *structure* — arms that route to different members — plus surcharges for
// the generic verb and the typed selector. So the number an agent watches is driven mostly by the
// fact that the three operations are still visibly three things.

namespace SampleSolution.Gate.Rules;

public sealed class GateShiftDispatchBeforeService
{
    private int _open;
    private int _filled;
    private int _cancelled;

    public void ApplyShiftAction(int shiftId, GateShiftAction action, string note)
    {
        switch (action)
        {
            case GateShiftAction.Open:
                Open(shiftId);
                break;
            case GateShiftAction.Fill:
                Fill(shiftId, note);
                break;
            case GateShiftAction.Cancel:
                Cancel(shiftId);
                break;
        }
    }

    private void Open(int shiftId) => _open += shiftId > 0 ? 1 : 0;

    private void Fill(int shiftId, string note) => _filled += note.Length;

    private void Cancel(int shiftId) => _cancelled++;

    public int OpenCount() => _open;
}

public enum GateShiftAction
{
    Open,
    Fill,
    Cancel,
}
