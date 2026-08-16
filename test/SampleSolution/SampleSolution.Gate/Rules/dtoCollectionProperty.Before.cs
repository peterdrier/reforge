// gate1: dtoCollectionProperty
// gate1-gameable: the list becomes a delimited string. dtoCollectionProperty charges 2, the scalar
// that replaces it charges 1, and the total falls by one point per collection converted.
//
// The same values cross the boundary and there are now strictly more ways to get them wrong: the
// consumer has to know the delimiter, the producer has to know what happens when a tag contains
// one, and neither is written down anywhere the compiler can see. A serialized collection is a
// collection whose contract moved into prose.
//
// The rule's premise is sound — a collection on a DTO is a wider promise than a scalar, because the
// cardinality is unbounded and every element is part of the contract. The premise just isn't what
// gets measured. What gets measured is whether the declared type is System.Collections.Generic.*,
// and that is a property of the declaration rather than of the promise.

namespace SampleSolution.Gate.Rules;

public sealed class GateArticleDto
{
    public int ArticleId { get; set; }
    public List<string> Tags { get; set; } = new();
}
