// Satellite of projectReference.Before.cs, compiled as SampleSolution.Camp when the variant is
// scored alone. A static rule rather than a service: nothing here is injected, so the edge is
// priced by projectReference alone.

namespace SampleSolution.Gate.Rules;

public static class CampFeeRules
{
    public static decimal LateFee(int daysLate) => daysLate * 5m;
}
