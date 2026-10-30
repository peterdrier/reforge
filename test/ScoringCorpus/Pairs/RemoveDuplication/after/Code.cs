namespace ScoringCorpus.RemoveDuplication.After;

public sealed class Signup
{
    private readonly List<string> _people = new();
    private readonly List<string> _teams = new();

    public void AddPerson(string name)
    {
        Validate(name);
        _people.Add(name);
    }

    public void AddTeam(string name)
    {
        Validate(name);
        _teams.Add(name);
    }

    public int Count() => _people.Count + _teams.Count;

    private static void Validate(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("empty");
        if (name.Length > 40) throw new ArgumentException("long");
        foreach (var ch in name)
            if (!char.IsLetter(ch) && ch != ' ') throw new ArgumentException("bad char");
    }
}
