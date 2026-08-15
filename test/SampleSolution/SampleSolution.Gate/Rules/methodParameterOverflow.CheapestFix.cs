// gate1: methodParameterOverflow
//
// The laziest fix, and the one an LLM reaches for first: wrap the argument list in a parameter
// object. The signature now has one parameter and methodParameterOverflow stops firing.
//
// Nothing about the boundary changed. All six values still cross it, the input type adds no
// invariant that would justify its existence — its constructor is a direct assignment bag with no
// validation — and a caller must now construct it. parameterBagInput and optionsBag charge for
// exactly that, which is what pass 7 was built for, so the total goes up rather than down.

namespace SampleSolution.Gate.Rules;

public sealed class GateOverflowCheapestFixService
{
    public string RegisterCamper(GateCamperRegistrationInput input)
        => $"{input.FirstName} {input.LastName}";
}

public sealed class GateCamperRegistrationInput
{
    public GateCamperRegistrationInput(
        string firstName,
        string lastName,
        int age,
        int cabinId,
        int sessionId,
        int emergencyContactId)
    {
        FirstName = firstName;
        LastName = lastName;
        Age = age;
        CabinId = cabinId;
        SessionId = sessionId;
        EmergencyContactId = emergencyContactId;
    }

    public string FirstName { get; }
    public string LastName { get; }
    public int Age { get; }
    public int CabinId { get; }
    public int SessionId { get; }
    public int EmergencyContactId { get; }
}
