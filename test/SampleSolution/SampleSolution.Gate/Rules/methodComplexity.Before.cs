// gate1: methodComplexity
//
// One method at cognitive complexity 11: a loop over five independent conditions. Close to the
// size methodCount names as the right bite, so there is nothing for a split to buy.

namespace SampleSolution.Gate.Rules;

public sealed class GateTallyBeforeService
{
    public int Score(IReadOnlyList<int> values)
    {
        int score = 0;
        foreach (var v in values)
        {
            if (v < 0) continue;
            if (v % 2 == 0) score += 2;
            if (v % 3 == 0) score += 3;
            if (v % 5 == 0) score += 5;
            if (v > 100) score += 10;
        }
        return score;
    }
}
