// gate1: methodCount
// gate1-gameable: a declaration with two or more callers skips the flat charge, because merging
// reused code away means a copy per caller. The cheapest fix manufactures the second caller: an
// unrelated method calls the helper and throws the result away. The call adds no branching, so
// nothing else moves and the total drops by one flat charge. Nothing was reused; one pointless
// call was added. The hole is worth at most methodCount per declaration, and closing it means
// telling a real caller from one whose result is discarded.
//
// The Before: a tiny helper with one caller, which pays the flat charge.

namespace SampleSolution.Gate.Rules;

public sealed class GateLabelBeforeService
{
    public string Describe(int code) => Prefix(code) + code;

    public int Count(IReadOnlyList<int> codes) => codes.Count;

    private static string Prefix(int code) => code < 0 ? "-" : "+";
}
