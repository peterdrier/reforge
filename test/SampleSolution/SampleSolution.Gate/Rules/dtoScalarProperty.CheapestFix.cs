// gate1: dtoScalarProperty
//
// Six named, typed properties become one string-to-string dictionary. dtoScalarProperty goes from
// six charges to none and dtoCollectionProperty picks up one, so the total falls.
//
// Every value that crossed the boundary before crosses it now. What was lost is everything that
// made them a contract: a consumer can no longer discover the field names from the type, the
// compiler can no longer catch a misspelled one, Age is no longer an int, and removing a field is
// no longer a breaking change anyone can see. By any reading of what "durable public surface"
// means, this is more of it, not less — the surface simply stopped being expressed in a form the
// counter can count.
//
// The counting is the problem. dtoScalarProperty charges per property because a wide DTO is a wide
// contract, which is true, but it makes property *count* the target, and count is trivially
// reducible without reducing what the count was standing in for. A rule that charged for the number
// of distinct values crossing the boundary — dictionary entries included, wherever they can be
// determined — would price both shapes the same and have nothing to offer an agent here.

namespace SampleSolution.Gate.Rules;

public sealed class GateProfileCheapestFixDto
{
    public Dictionary<string, string> Fields { get; set; } = new();
}
