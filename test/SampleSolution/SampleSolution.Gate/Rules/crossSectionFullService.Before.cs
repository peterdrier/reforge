// gate1: crossSectionFullService
// gate1-gameable: the constructor parameter becomes a settable property. Same dependency, same
// assembly reference, same calls, same section boundary crossed — the rule only reads constructor
// parameters, so moving the injection point one line down makes the coupling invisible to it.
//
// The design is worse afterwards, not merely no better. A constructor parameter is a statement that
// the object cannot exist without the dependency, checked at compile time and satisfied once. A
// settable property is a statement that it can, and the guarantee is replaced by a null reference
// at whatever moment someone forgot. Nothing was decoupled; the dependency stopped being declared.
//
// The hole is the whole detection surface: ScoreDependencyUse iterates c.Type.Constructors and
// nothing else, so property injection, setter injection, service-location and a method parameter
// are all free of every rule in the dependency-use family. crossSectionFullService is only the
// cheapest one to demonstrate it with — crossSectionRepository at 25 pays five times better for
// exactly the same edit.

namespace SampleSolution.Gate.Rules;

public sealed class GateInvoiceBeforeService
{
    private readonly ICampBillingService _billing;

    public GateInvoiceBeforeService(ICampBillingService billing) => _billing = billing;

    public int TotalDue(int campId) => _billing.BalanceFor(campId);
}
