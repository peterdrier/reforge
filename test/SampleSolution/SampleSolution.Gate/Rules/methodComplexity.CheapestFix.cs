// gate1: methodComplexity
//
// The shatter: every condition becomes its own one-branch helper, so no declaration reads above
// complexity 1 and the curve charges almost nothing. Nothing was simplified: the same five tests
// run in the same order, now behind five more names a reader has to follow. The flat charge per
// declaration is what makes this cost more than the method it replaced.

namespace SampleSolution.Gate.Rules;

public sealed class GateTallyCheapestFixService
{
    public int Score(IReadOnlyList<int> values)
    {
        int score = 0;
        foreach (var v in values) score += ScoreOne(v);
        return score;
    }

    private static int ScoreOne(int v) => v < 0 ? 0 : Even(v) + Third(v) + Fifth(v) + Large(v);

    private static int Even(int v) => v % 2 == 0 ? 2 : 0;

    private static int Third(int v) => v % 3 == 0 ? 3 : 0;

    private static int Fifth(int v) => v % 5 == 0 ? 5 : 0;

    private static int Large(int v) => v > 100 ? 10 : 0;
}
