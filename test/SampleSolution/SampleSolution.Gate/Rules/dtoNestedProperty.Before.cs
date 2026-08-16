// gate1: dtoNestedProperty
// gate1-gameable: the nested DTO property becomes a string holding the same object serialized.
// dtoNestedProperty charges 3, the scalar that replaces it charges 1, and the nested type is still
// declared and still charged for itself — so the only thing the two points bought was the compiler
// knowing what was inside the property.
//
// Both files declare the address type, unreferenced in the fix, because that is what an agent
// flattening one property actually leaves behind: the type has other callers and deleting it is not
// part of the edit. Holding it constant is also what makes the pair measure one thing — the
// property's classification — instead of the removal of a whole type.
//
// This is the worst of the three DTO dodges. dtoScalarProperty's dictionary fix and
// dtoCollectionProperty's CSV fix at least keep the values addressable by name; a JSON string is
// opaque to every consumer and to every tool, including this one, which is precisely why it scores
// as nothing. The rule charges 3 for a nested type because a nested type is a bigger promise than a
// scalar, and then prices the strictly larger promise of "some JSON" at 1.

namespace SampleSolution.Gate.Rules;

public sealed class GateAddressDto
{
    public string City { get; set; } = "";
    public string PostalCode { get; set; } = "";
}

public sealed class GateCustomerDto
{
    public int CustomerId { get; set; }
    public GateAddressDto Address { get; set; } = new();
}
