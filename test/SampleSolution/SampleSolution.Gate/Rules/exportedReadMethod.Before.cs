// gate1: exportedReadMethod
// gate1-gameable: three read charges collapse into one enum-dispatched getter and nothing prices the
// dispatch, because actionDispatcher and mutationModeParameter exempt read methods by design (a
// read whose arms share a base query is a legitimate shape). The dispatcher here shares nothing
// between arms, so the exemption is doing the agent's work for it. Closing this means teaching
// actionDispatcher to price a read dispatcher whose arms have disjoint bodies.
//
// Three published reads on an exported type. The rule charges per public read method.

namespace SampleSolution.Gate.Rules;

public sealed class GateRosterBeforeReader
{
    public string GetName(int camperId) => camperId.ToString();

    public int GetAge(int camperId) => camperId;

    public string GetCabin(int camperId) => "A";
}
