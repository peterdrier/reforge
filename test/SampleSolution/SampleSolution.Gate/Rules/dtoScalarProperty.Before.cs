// gate1: dtoScalarProperty
// gate1-gameable: replacing the six typed properties with one untyped collection satisfies the
// rule and costs a single dtoCollectionProperty charge. Six values still cross the boundary; the
// only thing removed is the type system's knowledge of what they are.
//
// A second dodge, not fixtured here because one pair holds one cheapest fix: hoisting the six
// properties onto a base class whose name matches no dto pattern also drops the charge to zero,
// because the rule reads GetMembers() and GetMembers() does not return inherited members. That one
// is a scoping bug and worth fixing on its own terms; the collection dodge below is the deeper
// problem, because the rule is doing exactly what it was written to do.

namespace SampleSolution.Gate.Rules;

public sealed class GateProfileDto
{
    public string FirstName { get; set; } = "";
    public string LastName { get; set; } = "";
    public int Age { get; set; }
    public int CabinId { get; set; }
    public int SessionId { get; set; }
    public string Nickname { get; set; } = "";
}
