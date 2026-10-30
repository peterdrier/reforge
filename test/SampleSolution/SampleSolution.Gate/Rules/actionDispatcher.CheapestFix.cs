// gate1: actionDispatcher
//
// The rule recognises a selector by its type. The laziest way to stop it is to keep the fold and
// take the type away: `string action` selects the identical three operations at the identical call
// sites, and the compiler no longer knows the legal set or rejects a misspelling.

namespace SampleSolution.Gate.Rules;

public sealed class GateShiftDispatchCheapestFixService
{
    private int _open;
    private int _filled;
    private int _cancelled;

    public void ApplyShiftAction(int shiftId, string action, string note)
    {
        switch (action)
        {
            case "Open":
                Open(shiftId);
                break;
            case "Fill":
                Fill(shiftId, note);
                break;
            case "Cancel":
                Cancel(shiftId);
                break;
        }
    }

    private void Open(int shiftId) => _open += shiftId > 0 ? 1 : 0;

    private void Fill(int shiftId, string note) => _filled += note.Length;

    private void Cancel(int shiftId) => _cancelled++;

    public int OpenCount() => _open;
}
