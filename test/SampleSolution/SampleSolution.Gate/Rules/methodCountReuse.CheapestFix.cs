// gate1: methodCount
//
// `Count` now calls `Prefix` for each code and discards the result, so `Prefix` has two callers
// and counts as reused.

namespace SampleSolution.Gate.Rules;

public sealed class GateLabelCheapestFixService
{
    public string Describe(int code) => Prefix(code) + code;

    public int Count(IReadOnlyList<int> codes)
    {
        foreach (var c in codes) _ = Prefix(c);
        return codes.Count;
    }

    private static string Prefix(int code) => code < 0 ? "-" : "+";
}
