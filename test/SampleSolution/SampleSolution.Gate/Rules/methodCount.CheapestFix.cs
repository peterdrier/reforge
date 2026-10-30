// gate1: methodCount
//
// The consolidation: the three helpers are pasted into the entry point so there are three fewer
// declarations to count. Each body now sits two levels deeper, inside the loop, and all of it is
// one method a reader has to hold at once. The convex curve is what makes this cost more than the
// three declarations it saved.

namespace SampleSolution.Gate.Rules;

public sealed class GateSettlementCheapestFixService
{
    public int Settle(IReadOnlyList<int> entries)
    {
        int total = 0;
        foreach (var e in entries)
        {
            int c = 0;
            if (e > 0)
            {
                for (int i = 0; i < e; i++)
                {
                    if (i % 2 == 0 && i > 3) c += 2;
                    else if (i % 3 == 0) c += 3;
                }
                c = c > 100 ? 100 : c;
            }

            int d = 0;
            if (e < 0)
            {
                for (int i = e; i < 0; i++)
                {
                    if (i % 2 == 0 || i < -50) d -= 2;
                    else if (i % 5 == 0) d -= 5;
                }
                d = d < -100 ? -100 : d;
            }

            int f = 0;
            if (e == 0) f = 1;
            else
            {
                while (f < 10)
                {
                    if (e % 7 == 0 && f > 2) f += 3;
                    else if (e % 11 == 0) f += 4;
                    else f++;
                }
            }

            total += c + d + f;
        }
        return total;
    }
}
