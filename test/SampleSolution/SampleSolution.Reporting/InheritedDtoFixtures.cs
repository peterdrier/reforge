namespace SampleSolution.Reporting;

// Issue #29 (3b): DTO property scoring iterated GetMembers(), which does not return inherited
// members. Hoisting a DTO's properties onto a base class whose name matches no DTO pattern
// therefore zeroed the charge while the published shape stayed identical — a consumer still reads
// every one of these off the derived type.
//
// Two shapes, because the hole had two depths.

// Depth 1: some properties hoisted. The derived type still has one of its own, so it still looks
// like a data carrier and still paid publicDtoType — but only for the property it declared.
public class ReportEnvelopeBase
{
    public Guid Id { get; set; }
    public string Title { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}

public sealed class PartiallyHoistedReportInfo : ReportEnvelopeBase
{
    public string Summary { get; set; } = "";
}

// Depth 2, and strictly cheaper: EVERY property hoisted. The derived type's own public property
// count is then zero, so it stopped looking like a data carrier at all and the publicDtoType charge
// disappeared along with the per-property ones. Same published shape as the type above.
public class FullyHoistedBase
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public int Sequence { get; set; }
}

public sealed class FullyHoistedReportInfo : FullyHoistedBase
{
}

// Control: a base carrying behaviour is not a data carrier, whichever type declares the method.
// A consumer can call it, which is the same reason declared behaviour disqualifies a type.
public class ReportBehaviourBase
{
    public Guid Id { get; set; }
    public string Render() => "";
}

public sealed class NotADataCarrierInfo : ReportBehaviourBase
{
    public string Title { get; set; } = "";
}
