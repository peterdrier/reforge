namespace ScoringCorpus.RenameMembers.Before;

public sealed class Roster
{
    private readonly List<string> _names = new();

    public void Add(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        if (_names.Contains(name)) return;
        _names.Add(name.Trim());
    }

    public int Count() => _names.Count(n => n.Length > 0);
}
