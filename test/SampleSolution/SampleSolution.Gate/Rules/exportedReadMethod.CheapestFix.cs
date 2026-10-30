// gate1: exportedReadMethod
//
// The laziest way to make a per-method charge smaller: one getter, a field selector, and the
// caller picks with an enum. exportedReadMethod drops from three charges to one. Nothing was
// removed, callers still choose between the three facts, and the return type had to widen to
// object to fit them all through one door.
//
// actionDispatcher prices it: the arms route to three members that share nothing, which is the
// three reads still there behind a selector. A read whose arms share a base member is not charged.

namespace SampleSolution.Gate.Rules;

public sealed class GateRosterCheapestFixReader
{
    public object GetField(int camperId, GateCamperField field)
    {
        switch (field)
        {
            case GateCamperField.Name:
                return Name(camperId);
            case GateCamperField.Age:
                return Age(camperId);
            default:
                return Cabin(camperId);
        }
    }

    private string Name(int camperId) => camperId.ToString();

    private int Age(int camperId) => camperId;

    private string Cabin(int camperId) => "A";
}

public enum GateCamperField
{
    Name,
    Age,
    Cabin,
}
