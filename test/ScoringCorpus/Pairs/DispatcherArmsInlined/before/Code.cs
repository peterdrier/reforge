namespace ScoringCorpus.DispatcherArmsInlined.Before;

public sealed class ShiftBoard
{
    private int _open;
    private int _filled;
    private int _cancelled;

    public void Apply(int shiftId, ShiftAction action, string note)
    {
        switch (action)
        {
            case ShiftAction.Open:
                Open(shiftId);
                break;
            case ShiftAction.Fill:
                Fill(note);
                break;
            case ShiftAction.Cancel:
                Cancel();
                break;
        }
    }

    private void Open(int shiftId) => _open += shiftId > 0 ? 1 : 0;
    private void Fill(string note) => _filled += note.Length;
    private void Cancel() => _cancelled++;
    public int OpenCount() => _open + _filled + _cancelled;
}

public enum ShiftAction { Open, Fill, Cancel }
