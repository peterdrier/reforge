namespace ScoringCorpus.DispatcherArmsInlined.After;

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
                _open += shiftId > 0 ? 1 : 0;
                break;
            case ShiftAction.Fill:
                _filled += note.Length;
                break;
            case ShiftAction.Cancel:
                _cancelled++;
                break;
        }
    }

    public int OpenCount() => _open + _filled + _cancelled;
}

public enum ShiftAction { Open, Fill, Cancel }
