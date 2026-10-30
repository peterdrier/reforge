namespace ScoringCorpus.SharedWorkReadMerge.After;

public sealed class CamperCards
{
    private readonly Dictionary<int, CamperRow> _store = new();

    public object Get(int id, CamperField field)
    {
        var row = Load(id);
        return field switch
        {
            CamperField.Name => row.Name,
            CamperField.Age => row.Age,
            _ => row.Cabin,
        };
    }

    private CamperRow Load(int id) => _store.TryGetValue(id, out var row) ? row : new CamperRow("", 0, "none");
}

public sealed record CamperRow(string Name, int Age, string Cabin);

public enum CamperField { Name, Age, Cabin }
