namespace ScoringCorpus.RenameMembers.After;

public sealed class CampRoster
{
    private readonly List<string> _names = new();

    public void Enrol(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        if (_names.Contains(name)) return;
        _names.Add(name.Trim());
    }

    public int Size() => _names.Count(n => n.Length > 0);
}
