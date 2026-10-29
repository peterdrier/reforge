// gate1: exportedReadMethod
//
// Three published reads on an exported type. The rule charges per public read method.

namespace SampleSolution.Gate.Rules;

public sealed class GateRosterBeforeReader
{
    public string GetName(int camperId) => camperId.ToString();

    public int GetAge(int camperId) => camperId;

    public string GetCabin(int camperId) => "A";
}
