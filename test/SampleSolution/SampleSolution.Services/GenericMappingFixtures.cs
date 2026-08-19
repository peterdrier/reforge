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
