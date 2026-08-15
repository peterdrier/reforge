// gate1: applicationServiceMethod
//
// Three published operations on a service. The rule charges per public method, so the number an
// agent sees is three times the weight — and the obvious way to make three into one is not to
// remove any of the operations.

namespace SampleSolution.Gate.Rules;

public sealed class GateRosterBeforeService
{
    public void AddCamper(int camperId) { }

    public void RemoveCamper(int camperId) { }

    public void RenameCamper(int camperId, string name) { }
}
