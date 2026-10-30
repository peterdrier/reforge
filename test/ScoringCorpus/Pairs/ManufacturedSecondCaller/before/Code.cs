namespace ScoringCorpus.ManufacturedSecondCaller.Before;

public sealed class LabelService
{
    public string Describe(int code) => Prefix(code) + code;
    public int Count(IReadOnlyList<int> codes) => codes.Count;

    private static string Prefix(int code) => code < 0 ? "-" : "+";
}
