// gate1: publicDtoType
//
// The Dto suffix is dropped. That is the entire edit: byte for byte, the declaration below is the
// Before with four characters removed from the type name.
//
// The type now matches no classification pattern, so it is never tagged, so nothing in the durable
// surface pass looks at it. publicDtoType goes to zero and the three property charges go with it.

namespace SampleSolution.Gate.Rules;

public sealed class GateShipment
{
    public int ShipmentId { get; set; }
    public string Carrier { get; set; } = "";
    public string TrackingNumber { get; set; } = "";
}
