// gate1: tupleReturn
//
// A published method handing back an anonymous tuple. The shape is unnamed at the boundary, so
// every consumer re-invents what the fields mean.

namespace SampleSolution.Gate.Rules;

public sealed class GateTupleReturnBeforeService
{
    public (string Name, int Count) Summarize() => ("gate", 1);
}
