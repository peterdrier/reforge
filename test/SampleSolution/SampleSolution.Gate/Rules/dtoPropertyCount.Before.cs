// gate1: dtoScalarProperty, publicDtoType
//
// A flat published DTO: six scalar properties, each one a thing a consumer can bind to and a thing
// that cannot be renamed without breaking them.

namespace SampleSolution.Gate.Rules;

public sealed class GateCamperInfo
{
    public string FirstName { get; set; } = "";
    public string LastName { get; set; } = "";
    public int Age { get; set; }
    public int CabinId { get; set; }
    public int SessionId { get; set; }
    public string Notes { get; set; } = "";
}
