namespace ScoringCorpus.SplitScatterAcrossTypes.After;

public sealed class Meter
{
    public int Total(IReadOnlyList<int> values)
        => AlphaRule.Alpha(values) + BravoRule.Bravo(values) + CharlieRule.Charlie(values) + DeltaRule.Delta(values) + EchoRule.Echo(values);
}

internal static class AlphaRule
{
    internal static int Alpha(IReadOnlyList<int> values)
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
}

internal static class BravoRule
{
    internal static int Bravo(IReadOnlyList<int> values)
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
}

internal static class CharlieRule
{
    internal static int Charlie(IReadOnlyList<int> values)
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
}

internal static class DeltaRule
{
    internal static int Delta(IReadOnlyList<int> values)
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
}

internal static class EchoRule
{
    internal static int Echo(IReadOnlyList<int> values)
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
