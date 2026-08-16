// The whole edit is moving one identifier out of the constructor and into a property. Whatever was
// resolving the Camp service still resolves it, the container still wires it, the call is unchanged,
// and Gate still cannot be built without a reference to Camp.
//
// What actually changed is that the dependency stopped being required. `new GateInvoiceCheapestFixService()`
// compiles now, and TotalDue throws at some later moment instead of the constructor refusing to run.
//
// (The interface is named ICampInvoicingService rather than ICampBillingService only because both
// variants' files share one assembly in the full sample solution — see the satellite.)

namespace SampleSolution.Gate.Rules;

public sealed class GateInvoiceCheapestFixService
{
    public ICampInvoicingService? Billing { get; set; }

    public int TotalDue(int campId) => Billing!.BalanceFor(campId);
}
