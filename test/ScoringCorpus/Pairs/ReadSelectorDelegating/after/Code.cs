namespace ScoringCorpus.ReadSelectorDelegating.After;

public sealed class CamperReader
{
    private readonly Dictionary<int, string> _names = new();
    private readonly Dictionary<int, int> _ages = new();
    private readonly Dictionary<int, string> _cabins = new();

    public object Get(int id, CamperField field)
    {
        switch (field)
        {
            case CamperField.Name:
                return Name(id);
            case CamperField.Age:
                return Age(id);
            default:
                return Cabin(id);
        }
    }

    private string Name(int id) => _names.TryGetValue(id, out var n) ? n : "";
    private int Age(int id) => _ages.TryGetValue(id, out var a) ? a : 0;
    private string Cabin(int id) => _cabins.TryGetValue(id, out var c) ? c : "none";
}

public enum CamperField { Name, Age, Cabin }
