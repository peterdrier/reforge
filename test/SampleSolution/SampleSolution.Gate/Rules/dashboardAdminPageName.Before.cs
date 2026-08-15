// gate1: dashboardAdminPageName
// gate1-gameable: the rule matches on the method's name, and the cheapest edit that stops it
// matching is a rename. Nothing about the coupling it is meant to detect changes.
//
// The rule's target is real: a service method named after a screen has taken a dependency on the
// UI's shape, and it will change whenever that screen changes. The problem is what the rule looks
// at, which is the identifier.

namespace SampleSolution.Gate.Rules;

public sealed class GateNamingBeforeService
{
    public string GetDashboardData(int userId) => userId.ToString();
}
