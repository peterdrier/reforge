// gate1: dtoNestedProperty
//
// GateAddressCheapestFixDto Address -> string AddressJson. IsNestedDtoType requires the property's
// type to have SpecialType.None and be a source-declared class or struct; string is
// SpecialType.System_String, so the property falls through to dtoScalarProperty at 1 instead of 3.
//
// The address type is still declared, and still scores its own publicDtoType and two scalars, so
// the difference between this file and the Before is exactly the property's classification.

namespace SampleSolution.Gate.Rules;

public sealed class GateAddressCheapestFixDto
{
    public string City { get; set; } = "";
    public string PostalCode { get; set; } = "";
}

public sealed class GateCustomerCheapestFixDto
{
    public int CustomerId { get; set; }
    public string AddressJson { get; set; } = "";
}
