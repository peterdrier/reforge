// gate1: methodComplexity
//
// The shatter: every condition is its own one-branch helper, each called once, so no declaration
// reads above complexity 1 and the methodComplexity charge collapses. Nothing was simplified: the
// same twenty tests run in the same order behind twenty-five names a reader has to follow. The flat
// charge on 26 declarations is what keeps this from scoring below the monolith it replaced.

namespace SampleSolution.Gate.Rules;

public sealed class GateMeterCheapestFixService
{
    public int Total(IReadOnlyList<int> values)
        => Alpha(values) + Bravo(values) + Charlie(values) + Delta(values) + Echo(values);

    private static int Alpha(IReadOnlyList<int> values)
    {
        int s = 0;
        foreach (var v in values) s += AlphaOne(v) + AlphaTwo(v) + AlphaThree(v) + AlphaFour(v);
        return s;
    }

    private static int AlphaOne(int v) => v % 2 == 0 ? 1 : 0;

    private static int AlphaTwo(int v) => v % 3 == 0 ? 2 : 0;

    private static int AlphaThree(int v) => v % 5 == 0 ? 3 : 0;

    private static int AlphaFour(int v) => v % 7 == 0 ? 4 : -1;

    private static int Bravo(IReadOnlyList<int> values)
    {
        int s = 0;
        foreach (var v in values) s += BravoOne(v) + BravoTwo(v) + BravoThree(v) + BravoFour(v);
        return s;
    }

    private static int BravoOne(int v) => v % 3 == 0 ? 1 : 0;

    private static int BravoTwo(int v) => v % 4 == 0 ? 2 : 0;

    private static int BravoThree(int v) => v % 6 == 0 ? 3 : 0;

    private static int BravoFour(int v) => v % 11 == 0 ? 4 : -1;

    private static int Charlie(IReadOnlyList<int> values)
    {
        int s = 0;
        foreach (var v in values) s += CharlieOne(v) + CharlieTwo(v) + CharlieThree(v) + CharlieFour(v);
        return s;
    }

    private static int CharlieOne(int v) => v % 2 == 0 ? 1 : 0;

    private static int CharlieTwo(int v) => v % 5 == 0 ? 2 : 0;

    private static int CharlieThree(int v) => v % 9 == 0 ? 3 : 0;

    private static int CharlieFour(int v) => v % 13 == 0 ? 4 : -1;

    private static int Delta(IReadOnlyList<int> values)
    {
        int s = 0;
        foreach (var v in values) s += DeltaOne(v) + DeltaTwo(v) + DeltaThree(v) + DeltaFour(v);
        return s;
    }

    private static int DeltaOne(int v) => v % 4 == 0 ? 1 : 0;

    private static int DeltaTwo(int v) => v % 7 == 0 ? 2 : 0;

    private static int DeltaThree(int v) => v % 10 == 0 ? 3 : 0;

    private static int DeltaFour(int v) => v % 17 == 0 ? 4 : -1;

    private static int Echo(IReadOnlyList<int> values)
    {
        int s = 0;
        foreach (var v in values) s += EchoOne(v) + EchoTwo(v) + EchoThree(v) + EchoFour(v);
        return s;
    }

    private static int EchoOne(int v) => v % 3 == 0 ? 1 : 0;

    private static int EchoTwo(int v) => v % 8 == 0 ? 2 : 0;

    private static int EchoThree(int v) => v % 11 == 0 ? 3 : 0;

    private static int EchoFour(int v) => v % 19 == 0 ? 4 : -1;
}
