namespace ScoringCorpus.ReadSelectorInlineArms.After;

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
                return _names.TryGetValue(id, out var n) ? n : "";
            case CamperField.Age:
                return _ages.TryGetValue(id, out var a) ? a : 0;
            default:
                return _cabins.TryGetValue(id, out var c) ? c : "none";
        }
    }
}

public enum CamperField { Name, Age, Cabin }
