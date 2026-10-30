namespace ScoringCorpus.WriteSelectorInlineArms.Before;

public sealed class ShiftBoard
{
    private int _open;
    private int _filled;
    private int _cancelled;

    public void Open(int shiftId) => _open += shiftId > 0 ? 1 : 0;
    public void Fill(int shiftId, string note) => _filled += note.Length;
    public void Cancel(int shiftId) => _cancelled++;
    public int OpenCount() => _open + _filled + _cancelled;
}
