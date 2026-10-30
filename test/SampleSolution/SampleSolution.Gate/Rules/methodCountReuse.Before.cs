// gate1: methodCount
// gate1-gameable: a declaration with two or more callers skips the flat charge, because merging
// reused code away means a copy per caller. The cheapest fix manufactures the second caller: an
// unrelated method calls the helper and folds the result into its own by multiplying it by zero.
// A call whose result is discarded no longer counts, but one whose result is used pointlessly
// does, and telling the two apart takes more than syntax. The call adds no branching, so nothing
// else moves and the total drops by one flat charge. The hole is worth at most methodCount per
// declaration.
//
// The Before: a tiny helper with one caller, which pays the flat charge.

namespace SampleSolution.Gate.Rules;

public sealed class GateLabelBeforeService
{
    public string Describe(int code) => Prefix(code) + code;

    public int Count(IReadOnlyList<int> codes) => codes.Count;

    private static string Prefix(int code) => code < 0 ? "-" : "+";
}
