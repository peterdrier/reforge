namespace SampleSolution.Lodge.Contracts;

// The non-Contracts half of LodgeAmenityInfo (the other half is in Contracts/LodgeContracts.cs).
// Named so it sorts to the front of the assembly's compile items, which makes THIS the type's
// primary source location — so derivation only sees the Contracts/ declaration if it inspects
// every location rather than the primary one. CanonicalReadDtoDerivationTests asserts that
// precondition explicitly, so the fixture can't quietly stop testing anything.
public sealed partial class LodgeAmenityInfo
{
    public bool Available { get; set; }
}
