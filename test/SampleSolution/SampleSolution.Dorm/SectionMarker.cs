namespace SampleSolution.Shared;

// Deliberately shares its fully qualified name with the type of the same name in
// SampleSolution.Tent. Legal C#: both are internal, so no consumer can see either and nothing
// is ambiguous. This exists to prove the classifier dedupes per ASSEMBLY rather than per display
// name — keyed on the display name alone, one of the two would be dropped from scoring entirely,
// and an assembly whose every type collided would vanish from the section map.
internal sealed class SectionMarker
{
    public string Name { get; set; } = "";
}
