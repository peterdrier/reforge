// gate1: actionDispatcher
//
// The rule fires on arms that route to *distinct members*. The laziest way to stop that is not to
// un-fold the three operations — it is to delete the members and paste their bodies into the arms.
// Same door, same enum, same three behaviours, same call sites. The only thing removed is the part
// a reader could still see: three named units with their own signatures.
//
// Nothing here is a fix. The dispatcher is more entangled than it was, the three operations no
// longer have names, and `Fill`'s dependence on `note` — which the Before stated in a signature —
// is now something you learn by reading the middle of a switch.

namespace SampleSolution.Gate.Rules;

public sealed class GateShiftDispatchCheapestFixService
{
    private int _open;
    private int _filled;
    private int _cancelled;

    public void ApplyShiftAction(int shiftId, GateShiftActionMode action, string note)
    {
        switch (action)
        {
            case GateShiftActionMode.Open:
                _open += shiftId > 0 ? 1 : 0;
                break;
            case GateShiftActionMode.Fill:
                _filled += note.Length;
                break;
            case GateShiftActionMode.Cancel:
                _cancelled++;
                break;
        }
    }

    public int OpenCount() => _open;
}

public enum GateShiftActionMode
{
    Open,
    Fill,
    Cancel,
}
