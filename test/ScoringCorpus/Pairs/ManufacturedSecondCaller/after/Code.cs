namespace ScoringCorpus.ManufacturedSecondCaller.After;

public sealed class LabelService
{
    public string Describe(int code) => Prefix(code) + code;

    public int Count(IReadOnlyList<int> codes)
    {
        foreach (var c in codes) _ = Prefix(c);
        return codes.Count;
    }

    private static string Prefix(int code) => code < 0 ? "-" : "+";
}
