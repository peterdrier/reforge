// Satellite of projectReference.CheapestFix.cs. Camp still publishes the rule it published in the
// Before; Gate simply stopped calling it. Named differently only because both variants' files share
// one assembly in the full sample solution.

namespace SampleSolution.Gate.Rules;

public static class CampPenaltyRules
{
    public static decimal LateFee(int daysLate) => daysLate * 5m;
}
