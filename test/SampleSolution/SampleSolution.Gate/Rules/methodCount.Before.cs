// gate1: methodCount
//
// An entry point and three helpers, each around complexity 10: the size methodCount names as the
// right bite, which is where the flat charge and the curve balance.

namespace SampleSolution.Gate.Rules;

public sealed class GateSettlementBeforeService
{
    public int Settle(IReadOnlyList<int> entries)
    {
        int total = 0;
        foreach (var e in entries)
            total += Credit(e) + Debit(e) + Fee(e);
        return total;
    }

    private static int Credit(int e)
    {
        if (e <= 0) return 0;
        int c = 0;
        for (int i = 0; i < e; i++)
        {
            if (i % 2 == 0 && i > 3) c += 2;
            else if (i % 3 == 0) c += 3;
        }
        return c > 100 ? 100 : c;
    }

    private static int Debit(int e)
    {
        if (e >= 0) return 0;
        int d = 0;
        for (int i = e; i < 0; i++)
        {
            if (i % 2 == 0 || i < -50) d -= 2;
            else if (i % 5 == 0) d -= 5;
        }
        return d < -100 ? -100 : d;
    }

    private static int Fee(int e)
    {
        if (e == 0) return 1;
        int f = 0;
        while (f < 10)
        {
            if (e % 7 == 0 && f > 2) f += 3;
            else if (e % 11 == 0) f += 4;
            else f++;
        }
        return f;
    }
}
