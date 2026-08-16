// gate1: flagsControlFlow
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
