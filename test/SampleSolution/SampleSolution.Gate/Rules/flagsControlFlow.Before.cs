// gate1: flagsControlFlow
// gate1-gameable: the [Flags] argument is unpacked into one bool per flag. Same three independent
// decisions, same three tests, same behaviour — now positional, so getting the order wrong
// compiles. 22 -> 21.
//
// One point, and it is worth saying that the backstops nearly hold: flagsControlFlow charges
// 8 + 4 = 12 for three tests, and what replaces it is booleanParameter three times (9) plus
// methodParameterOverflow for the two extra parameters (2), which is 11. The gate fails by the
// smallest margin it can.
//
// Do not close this by nudging a weight. A one-point gap means the two shapes are priced as very
// nearly equivalent, which is roughly right — a flags enum and three bools are the same design,
// and neither is the one worth arguing for. The finding is that the rule is *directionless*, not
// that it is cheap: an agent moving between these two shapes should see no change at all, and any
// weight that makes one of them strictly cheaper just picks the other direction to be gamed in.
//
// A public mutation whose control flow is driven by a [Flags] enum: one argument carries an
// arbitrary subset of three independent decisions, and the body tests each one. The rule charges
// 8 plus 4 per flag test beyond two, so three tests is the smallest interesting case.

namespace SampleSolution.Gate.Rules;

[Flags]
public enum GateProfileFields
{
    None = 0,
    Name = 1,
    Email = 2,
    Status = 4,
}

public sealed class GateProfileBeforeService
{
    private int _names;
    private int _emails;
    private int _statuses;

    public void UpdateProfile(int profileId, GateProfileFields fields)
    {
        if (fields.HasFlag(GateProfileFields.Name)) _names += profileId > 0 ? 1 : 0;
        if (fields.HasFlag(GateProfileFields.Email)) _emails++;
        if (fields.HasFlag(GateProfileFields.Status)) _statuses++;
    }

    public int NameCount() => _names;
}
