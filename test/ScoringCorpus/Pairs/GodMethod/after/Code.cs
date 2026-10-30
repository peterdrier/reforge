namespace ScoringCorpus.GodMethod.After;

public sealed class Meter
{
    public int Total(IReadOnlyList<int> values)
    {
        int s = 0;
        foreach (var v in values)
        {
            if (v % 2 == 0) s += 1;
            if (v % 3 == 0) s += 2;
            if (v % 5 == 0) s += 3;
            if (v % 7 == 0) s += 4;
            else s -= 1;
        }
        foreach (var v in values)
        {
            if (v % 3 == 0) s += 1;
            if (v % 4 == 0) s += 2;
            if (v % 6 == 0) s += 3;
            if (v % 11 == 0) s += 4;
            else s -= 1;
        }
        foreach (var v in values)
        {
            if (v % 2 == 0) s += 1;
            if (v % 5 == 0) s += 2;
            if (v % 9 == 0) s += 3;
            if (v % 13 == 0) s += 4;
            else s -= 1;
        }
        foreach (var v in values)
        {
            if (v % 4 == 0) s += 1;
            if (v % 7 == 0) s += 2;
            if (v % 10 == 0) s += 3;
            if (v % 17 == 0) s += 4;
            else s -= 1;
        }
        foreach (var v in values)
        {
            if (v % 3 == 0) s += 1;
            if (v % 8 == 0) s += 2;
            if (v % 11 == 0) s += 3;
            if (v % 19 == 0) s += 4;
            else s -= 1;
        }
        return s;
    }
}
