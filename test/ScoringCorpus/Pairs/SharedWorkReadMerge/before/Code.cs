namespace ScoringCorpus.SharedWorkReadMerge.Before;

public sealed class CamperCards
{
    private readonly Dictionary<int, CamperRow> _store = new();

    public string GetName(int id) => Load(id).Name;
    public int GetAge(int id) => Load(id).Age;
    public string GetCabin(int id) => Load(id).Cabin;

    private CamperRow Load(int id) => _store.TryGetValue(id, out var row) ? row : new CamperRow("", 0, "none");
}

public sealed record CamperRow(string Name, int Age, string Cabin);
