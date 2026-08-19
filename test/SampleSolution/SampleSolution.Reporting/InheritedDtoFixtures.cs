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

// A constructed generic base: the base is itself a scored DTO, so the derived type must NOT be
// charged again for its properties. The key the scored-DTO set is built from is the declaration
// (`GenericEnvelopeInfo<T>`), while the base seen from here is the constructed `GenericEnvelopeInfo<int>` —
// different display strings, so querying with the constructed form silently misses.
public class GenericEnvelopeInfo<T>
{
    public Guid Id { get; set; }
    public string Label { get; set; } = "";
}

public sealed class ConstructedGenericReportInfo : GenericEnvelopeInfo<int>
{
    public string Extra { get; set; } = "";
}

// Indexer overloads share the name `Item`. They are distinct published properties, so a name-only
// de-duplication key would charge only the first.
public sealed class IndexedReportInfo
{
    public string Title { get; set; } = "";
    public string this[int index] => "";
    public string this[string key] => "";
}

// An event is behaviour a consumer can subscribe to, just as a method is behaviour it can call.
// With the walk climbing base types, ignoring events would admit this type as a pure data carrier
// on the strength of the inherited property alone.
public class EventingEnvelopeBase
{
    public Guid Id { get; set; }
    public event EventHandler? Changed;
}

public sealed class EventingReportInfo : EventingEnvelopeBase
{
}

// A default interface method is behaviour the type never declares anywhere, so no walk over
// declarations can see it — a consumer can still call it through the interface.
public interface IHasDefaultBehaviour
{
    string Describe() => "";
}

public sealed class DefaultMethodReportInfo : IHasDefaultBehaviour
{
    public string Title { get; set; } = "";
}

// An explicit interface implementation is `private` on the symbol and callable by anyone who casts.
// An accessibility filter therefore skips it, and the interface scan skips it too because the
// interface declaration is abstract — so a reject-list predicate misses it from both directions.
public interface IExplicitBehaviour
{
    string Describe();
}

public sealed class ExplicitlyImplementedReportInfo : IExplicitBehaviour
{
    public string Title { get; set; } = "";
    string IExplicitBehaviour.Describe() => "";
}

// A DTO whose only published properties are indexers. It must not fall between the two halves of
// the rule: not a data carrier (so no publicDtoType) yet never reached (so no per-indexer charge).
public sealed class IndexerOnlyReportInfo
{
    public string this[int index] => "";
}

// Derives from a base declared in a different project of the same solution. Its inherited
// properties are published by THIS section and must be charged to it.
public sealed class CrossProjectReportInfo : SampleSolution.Camp.CrossProjectEnvelopeBase
{
    public string Summary { get; set; } = "";

    // A property whose TYPE lives in another project of the solution. It is a nested DTO property
    // (weight 3), not a scalar one (weight 1) — deciding that by source location rather than by
    // assembly membership would misprice it wherever the reference is a compiled DLL.
    public SampleSolution.Camp.CrossProjectNestedPayload Payload { get; set; } = new();
}
