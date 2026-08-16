// gate1: flagsControlFlow
//
// The rule tests for a [Flags] enum, so the cheapest exit is to unpack it into one bool per flag.
// The method still takes an arbitrary subset of three independent decisions, still tests each one,
// and still does the same three things — the subset just arrives as three positional booleans
// instead of one named value.
//
// It is worse at the call site, which is the part the rule was never measuring:
// `UpdateProfile(id, GateProfileFields.Email)` becomes `UpdateProfile(id, false, true, false)`, and
// getting the order wrong now compiles. Whether the gate holds depends entirely on whether the
// backstops — booleanParameter per bool, methodParameterOverflow for the two extra parameters —
// add up to more than the flags charge they replaced.

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
