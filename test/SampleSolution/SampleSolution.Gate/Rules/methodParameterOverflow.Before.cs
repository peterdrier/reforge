// gate1: methodParameterOverflow
//
// Six parameters on a published service method. Four past the two-parameter allowance, so
// methodParameterOverflow charges one point each.

namespace SampleSolution.Gate.Rules;

public sealed class GateOverflowBeforeService
{
    public string RegisterCamper(
        string firstName,
        string lastName,
        int age,
        int cabinId,
        int sessionId,
        int emergencyContactId)
        => $"{firstName} {lastName} {age} {cabinId} {sessionId} {emergencyContactId}";
}
