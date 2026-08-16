// gate1: flagsControlFlow
//
// The rule tests for a [Flags] enum, so the cheapest exit is to unpack it into one bool per flag.
// The method still takes an arbitrary subset of three independent decisions, still tests each one,
// and still does the same three things — the subset just arrives as three positional booleans
// instead of one named value.
//
// It is worse at the call site, which is the part the rule was never measuring:
// `UpdateProfile(id, GateProfileFields.Email)` becomes `UpdateProfile(id, false, true, false)`, and
// getting the order wrong now compiles.
//
// The backstops nearly cover it and fall one point short: booleanParameter three times (9) plus
// methodParameterOverflow for the two extra parameters (2) is 11, against the 12 that
// flagsControlFlow charged. See the Before file for why that gap should not be closed with a
// weight.

namespace SampleSolution.Gate.Rules;

public sealed class GateProfileCheapestFixService
{
    private int _names;
    private int _emails;
    private int _statuses;

    public void UpdateProfile(int profileId, bool name, bool email, bool status)
    {
        if (name) _names += profileId > 0 ? 1 : 0;
        if (email) _emails++;
        if (status) _statuses++;
    }

    public int NameCount() => _names;
}
