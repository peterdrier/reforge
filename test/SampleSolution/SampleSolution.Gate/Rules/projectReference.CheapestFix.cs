// Gate restates Camp's rule privately and stops naming Camp. Two copies of the late-fee rule now,
// and the next change to Camp's will reach one of them.

namespace SampleSolution.Gate.Rules;

public sealed class GateFeeCheapestFixService
{
    public decimal FeeFor(int daysLate) => LateFee(daysLate);

    private static decimal LateFee(int daysLate) => daysLate * 5m;
}
