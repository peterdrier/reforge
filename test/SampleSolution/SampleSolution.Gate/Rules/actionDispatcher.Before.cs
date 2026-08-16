// gate1: actionDispatcher
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
