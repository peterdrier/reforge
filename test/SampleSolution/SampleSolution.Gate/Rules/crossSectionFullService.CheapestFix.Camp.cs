// Satellite of crossSectionFullService.CheapestFix.cs. The same shape as the Before's satellite —
// one exported single-method service interface in another section — under a different name, because
// inside the full sample solution every Gate fixture file compiles into one assembly and two
// declarations of ICampBillingService would collide.
//
// Same shape matters more than same name: the satellite's own charges are identical in both
// variants and cancel out of the delta, so what the pair measures is the injection point.

namespace SampleSolution.Gate.Rules;

public interface ICampInvoicingService
{
    int BalanceFor(int campId);
}
