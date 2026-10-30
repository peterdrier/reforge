// gate1: methodCount
//
// `Count` now calls `Prefix` and adds its length times zero, so `Prefix` has two callers that use
// its result and counts as reused.

namespace SampleSolution.Gate.Rules;

public sealed class GateLabelCheapestFixService
{
    public string Describe(int code) => Prefix(code) + code;

    public int Count(IReadOnlyList<int> codes) => codes.Count + Prefix(0).Length * 0;

    private static string Prefix(int code) => code < 0 ? "-" : "+";
}
