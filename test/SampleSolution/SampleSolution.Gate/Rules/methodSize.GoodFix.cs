// The middle arrangement: one method per tally, each at complexity 10, the threshold, and an entry
// point that adds them. No piece pays the tax and only six pay the flat charge, so this scores below
// both the monolith and the shatter.

namespace SampleSolution.Gate.Rules;

public sealed class GateMeterGoodFixService
{
    public int Total(IReadOnlyList<int> values)
        => Alpha(values) + Bravo(values) + Charlie(values) + Delta(values) + Echo(values);

    private static int Alpha(IReadOnlyList<int> values)
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
        return s;
    }

    private static int Bravo(IReadOnlyList<int> values)
    {
        int s = 0;
        foreach (var v in values)
        {
            if (v % 3 == 0) s += 1;
            if (v % 4 == 0) s += 2;
            if (v % 6 == 0) s += 3;
            if (v % 11 == 0) s += 4;
            else s -= 1;
        }
        return s;
    }

    private static int Charlie(IReadOnlyList<int> values)
    {
        int s = 0;
        foreach (var v in values)
        {
            if (v % 2 == 0) s += 1;
            if (v % 5 == 0) s += 2;
            if (v % 9 == 0) s += 3;
            if (v % 13 == 0) s += 4;
            else s -= 1;
        }
        return s;
    }

    private static int Delta(IReadOnlyList<int> values)
    {
        int s = 0;
        foreach (var v in values)
        {
            if (v % 4 == 0) s += 1;
            if (v % 7 == 0) s += 2;
            if (v % 10 == 0) s += 3;
            if (v % 17 == 0) s += 4;
            else s -= 1;
        }
        return s;
    }

    private static int Echo(IReadOnlyList<int> values)
    {
        int s = 0;
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
