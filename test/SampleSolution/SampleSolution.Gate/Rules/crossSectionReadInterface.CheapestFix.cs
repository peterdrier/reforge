// Camp's read interface is gone from the constructor and the calculation it was called for now
// lives here. Camp still publishes the interface (see the satellite) — Gate simply stopped asking.
//
// The private helper is invisible to every rule in the model: nothing charges for a method that is
// not surface, and nothing anywhere charges for the fact that this code already exists one assembly
// over. The measured result is that Gate got cheaper by removing a two-point dependency and paid
// nothing for the copy that replaced it.

namespace SampleSolution.Gate.Rules;

public sealed class GateHeadcountCheapestFixService
{
    private readonly List<GateCamperRow> _campers = new();

    public int ActiveHeadcount(int campId) => CountActive(campId);

    // The same rule for "active" that Camp's read interface implements, restated. Two copies now,
    // and the next change to Camp's definition will reach exactly one of them.
    private int CountActive(int campId)
    {
        int n = 0;
        foreach (var c in _campers)
            if (c.CampId == campId && c.Active) n++;
        return n;
    }
}

internal sealed class GateCamperRow
{
    public int CampId { get; set; }
    public bool Active { get; set; }
}
