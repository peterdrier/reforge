namespace ScoringCorpus.ShatterIntoOneLiners.After;

public sealed class Tally
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
