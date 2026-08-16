// gate1: dtoCollectionProperty
//
// List<string> Tags -> string TagsCsv. IsCollectionType tests the declared type's original
// definition against System.Collections.Generic.*, System.Collections.IEnumerable and
// System.Collections.Immutable.*, so a string carrying the same elements falls through to
// dtoScalarProperty at 1 point instead of 2.

namespace SampleSolution.Gate.Rules;

public sealed class GateArticleCheapestFixDto
{
    public int ArticleId { get; set; }
    public string TagsCsv { get; set; } = "";
}
