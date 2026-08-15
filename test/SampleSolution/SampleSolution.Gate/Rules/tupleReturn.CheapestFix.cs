// gate1: tupleReturn
//
// The laziest fix: promote the tuple to a named type. tupleReturn stops firing.
//
// This one is worth reading closely, because the fix is not obviously wrong — a named result type
// really is better than an anonymous tuple. What the gate insists on is that it not be scored as
// free. The two fields are now a published DTO with a published type name, which is more durable
// surface than a tuple was, not less: renaming a tuple element breaks nobody outside the method,
// renaming GateSummaryResult.Name breaks every consumer. The score says so.

namespace SampleSolution.Gate.Rules;

public sealed class GateTupleReturnCheapestFixService
{
    public GateSummaryResult Summarize() => new();
}

public sealed class GateSummaryResult
{
    public string Name { get; set; } = "";
    public int Count { get; set; }
}
