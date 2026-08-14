namespace SampleSolution.Lodge.Contracts.V2;

// Same SIMPLE name as SampleSolution.Lodge.Contracts.LodgeStayInfo, different namespace, same
// section. The anchor-preference comparator has to break this tie on full identity: it returns 0
// on both name and length, and List.Sort is not stable, so without a tiebreak the primary anchor
// would be whichever the enumeration happened to yield first.
public sealed class LodgeStayInfo
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
}
