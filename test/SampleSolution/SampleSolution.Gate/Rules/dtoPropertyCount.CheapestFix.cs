// gate1: dtoScalarProperty, publicDtoType
//
// The laziest fix: push five of the six properties down into a nested DTO. The parent's scalar
// count drops from six to one, which is the number an agent optimizing dtoScalarProperty would
// report as an improvement.
//
// Nothing left the boundary. The same six values are still published, one level deeper, and there
// is now a second public type to break consumers with. dtoNestedProperty charges for the extra hop
// and the nested type pays publicDtoType plus its own scalars, so the pair scores higher than the
// flat version it replaced.

namespace SampleSolution.Gate.Rules;

public sealed class GateCamperSummaryInfo
{
    public string FirstName { get; set; } = "";
    public GateCamperDetailInfo Detail { get; set; } = new();
}

public sealed class GateCamperDetailInfo
{
    public string LastName { get; set; } = "";
    public int Age { get; set; }
    public int CabinId { get; set; }
    public int SessionId { get; set; }
    public string Notes { get; set; } = "";
}
