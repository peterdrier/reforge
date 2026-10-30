namespace ScoringCorpus.SplitPartialAcrossFiles.After;

public sealed partial class Roster
{
    private readonly List<string> _names = new();

    public void Add(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        if (_names.Contains(name)) return;
        _names.Add(name.Trim());
    }
}
