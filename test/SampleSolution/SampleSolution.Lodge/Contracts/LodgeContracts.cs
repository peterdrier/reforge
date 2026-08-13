namespace SampleSolution.Lodge.Contracts;

// The SECOND canonical-read-DTO shape: no .Contracts assembly, just a Contracts/ folder inside the
// section's own assembly. Both shapes occur in the wild and both are structural, so derivation has
// to accept either.
//
// The name deliberately does NOT match the <Section>Info convention ("LodgeInfo"), so the
// section-shape analyzer can only resolve Lodge's primary anchor through the derived canonical set.
public sealed class LodgeStayInfo
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
}

// A Contracts folder is a LOCATION, not evidence of a published API. This type sits right next to
// the one above and is still unreachable from any other section, so it must never be derived as a
// canonical read DTO — the check is effective accessibility, never the folder or namespace name.
internal sealed class LodgeSecretInfo
{
    public Guid Id { get; set; }
    public string Notes { get; set; } = "";
}
