namespace ScoringCorpus.ReadSelectorDelegating.Before;

public sealed class CamperReader
{
    private readonly Dictionary<int, string> _names = new();
    private readonly Dictionary<int, int> _ages = new();
    private readonly Dictionary<int, string> _cabins = new();

    public string GetName(int id) => _names.TryGetValue(id, out var n) ? n : "";
    public int GetAge(int id) => _ages.TryGetValue(id, out var a) ? a : 0;
    public string GetCabin(int id) => _cabins.TryGetValue(id, out var c) ? c : "none";
}
