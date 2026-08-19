namespace SampleSolution.Services;

/// <summary>
/// A generic method for the collision check to match against. <c>Passthrough&lt;U&gt;(U)</c> and a
/// caller's <c>Passthrough&lt;T&gt;(T)</c> are the same signature to C#, which is what makes the
/// ordinal comparison in <c>MisplacedAnalyzer.SameParameterType</c> necessary: the two type parameters
/// are different symbols, so comparing them by identity called a decisive collision a near-miss.
/// </summary>
public class GenericMappingService
{
    public U Passthrough<U>(U value) => value;
}

/// <summary>
/// A DTO by <b>config rule only</b> — the name matches the default <c>*Result</c> pattern, while the
/// <c>Describe</c> method disqualifies it from the structural <c>IsDataCarrier</c> test. Reads of its
/// properties must still count as data, or a method that only maps it reads as behavior-hungry.
/// </summary>
public class RelocationSummaryResult
{
    public string Label { get; init; } = "";
    public int Count { get; init; }
    public string Kind { get; init; } = "";
    public string Note { get; init; } = "";

    public string Describe() => $"{Label} ({Count})";
}

/// <summary>
/// A second configured-only DTO, used to check that <b>calling</b> a method on such a type is a
/// behavior call. The config rule labels the type's role; it says nothing about its members.
/// </summary>
public class VerboseSummaryResult
{
    public string Label { get; init; } = "";

    public string Describe() => $"[{Label}]";
    public string Shout() => Label.ToUpperInvariant();
    public string Whisper() => Label.ToLowerInvariant();
}

/// <summary>
/// The <c>out</c> half of a ref-kind collision. C# refuses two declarations differing only in
/// <c>ref</c> vs <c>out</c> vs <c>in</c>, so a caller's <c>TryPassthrough(ref string)</c> cannot join
/// this type — comparing the enum exactly called that a near-miss.
/// </summary>
public class RefKindTargetService
{
    public bool TryPassthrough(out string value)
    {
        value = "";
        return true;
    }

    public string Echo(string value) => value;
}

/// <summary>
/// A base with behavior on it, so a DTO deriving from it fails the structural carrier test and the
/// config rule is the only thing marking the derived type as data.
/// </summary>
public class BehaviorfulRowBase
{
    public int Id { get; init; }
    public string Slug { get; init; } = "";

    public string Recompute() => Slug;
}

/// <summary>
/// A configured-only DTO whose useful properties are <b>inherited</b>. Read through this type they are
/// data; read off <c>BehaviorfulRowBase</c>, which no rule names, they counted as behavior calls.
/// </summary>
public class InheritedSummaryResult : BehaviorfulRowBase
{
    public string Label { get; init; } = "";
}

/// <summary>
/// Constructed rather than called. Three of these in a method are three touches on this section, and
/// counting only member accesses missed them entirely — <c>new T()</c> carries its constructor on the
/// creation expression, while the type name inside it binds to the type.
/// </summary>
public class ConstructedWorkItem
{
    public ConstructedWorkItem(string label) => Label = label;

    public string Label { get; }

    public string Render() => $"<{Label}>";
}

/// <summary>
/// An enum in another section, for the data/behavior split. Reading its members is reading constants —
/// counted as calls they both mis-describe the evidence and let a type that cannot declare a method win
/// the destination contest.
/// </summary>
public enum WorkItemSize
{
    Small,
    Medium,
    Large,
    Enormous
}

/// <summary>
/// Reached through an indexer. The indexer symbol hangs off the element-access expression, not off any
/// identifier, so reads through it were measured as nothing. <c>Count</c> is a method so the type is not
/// a data carrier and the reads count as behavior.
/// </summary>
public class SlotTable
{
    private readonly string[] _slots = { "a", "b", "c", "d" };

    public string this[int index] => _slots[index];

    public int Count() => _slots.Length;
}

/// <summary>
/// A base declaring two overloads that differ only in how the parameter is passed, one of which supplies
/// a derived type's interface member. The contract pins that overload and nothing else.
/// </summary>
public interface IRefOverloadContract
{
    string HandleRefOverload(ref int value);
}

/// <summary>
/// A contract supplied to a <b>private</b> derived type. The classifier drops effectively private types,
/// so an index built from that list never saw this pin — while the compiler does not care who can see
/// the implementer: moving the base method still breaks the build.
/// </summary>
public interface IPrivatelyImplementedContract
{
    string DescribePrivately(int value);
}

/// <summary>
/// Same-named nested types in one namespace. A destination name assembled from namespace and simple name
/// alone renders both identically, which is what the qualified-name fix had to keep apart.
/// </summary>
public static class OuterA
{
    public class SharedName
    {
        public string Describe() => "a";
    }
}

public static class OuterB
{
    public class SharedName
    {
        public string Describe() => "b";
    }
}
