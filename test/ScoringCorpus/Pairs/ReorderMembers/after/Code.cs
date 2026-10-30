namespace ScoringCorpus.ReorderMembers.After;

public sealed class Roster
{
    public int Count() => _names.Count(n => n.Length > 0);

    public void Add(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        if (_names.Contains(name)) return;
        _names.Add(name.Trim());
    }

    private readonly List<string> _names = new();
}
