// gate1: dashboardAdminPageName
//
// One identifier changed. Not the return type, not the parameters, not the caller, not what the
// method assembles or which screen it assembles it for — an agent doing this does not even need to
// read the body. The screen coupling the rule set out to charge for is untouched, and the charge is
// gone.
//
// This is the cleanest possible demonstration of the shape the gate looks for: the rule reads a
// property of the *name* and treats it as evidence of a property of the *design*, so the name is
// where an agent will pay. Rules keyed on identifiers can only ever cost an agent a rename.
//
// It is left recorded rather than repaired because repair is a design question — either the rule
// finds screen coupling structurally (a return shape assembled for one page, a caller in exactly
// one controller action) or it is a lint that belongs in a naming convention, not in a score.

namespace SampleSolution.Gate.Rules;

public sealed class GateNamingCheapestFixService
{
    public string GetOverviewData(int userId) => userId.ToString();
}
