// gate1: applicationServiceMethod
//
// The laziest way to make a per-method charge smaller: keep every operation and publish one door
// into all of them. applicationServiceMethod drops from three charges to one, which is the number
// the agent was watching.
//
// This is the case the whole gate was built around, and the one it has to get right. Nothing was
// removed — the three operations are still here as private members, callers still pick between
// them, and the picking simply moved from the method name into an argument. The boundary got
// *worse*: `AddCamper(id)` cannot be called wrongly, `ApplyCamperAction(id, action, name)` can be
// called with a name that means nothing for two of its three actions, and the compiler stopped
// being able to tell.
//
// actionDispatcher and mutationModeParameter exist to charge for precisely that trade, so the total
// rises. If they ever stop firing here, applicationServiceMethod becomes a rule that pays an agent
// to hide a service behind a switch statement.

namespace SampleSolution.Gate.Rules;

public sealed class GateRosterCheapestFixService
{
    public void ApplyCamperAction(int camperId, GateCamperAction action, string name)
    {
        switch (action)
        {
            case GateCamperAction.Add:
                Add(camperId);
                break;
            case GateCamperAction.Remove:
                Remove(camperId);
                break;
            case GateCamperAction.Rename:
                Rename(camperId, name);
                break;
        }
    }

    private void Add(int camperId) { }

    private void Remove(int camperId) { }

    private void Rename(int camperId, string name) { }
}

public enum GateCamperAction
{
    Add,
    Remove,
    Rename,
}
