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

// Exported, in a Contracts/ folder, one public property and no methods of its own — but a consumer
// holding one gets Add/Remove/Insert through the base. Behavior inherited is still behavior, so this
// is not a data carrier and must not be published as a canonical read DTO. (Its name matches no dto
// glob either, so the behavioral check is the only thing that could admit it.)
public sealed class LodgeOccupancyTally : List<int>
{
    public int Total { get; set; }
}

// Behavior a consumer can invoke that is not an ordinary method. None of these three is a data
// carrier, and each would slip through a check that only looked at MethodKind.Ordinary or that
// counted any property at all.
public sealed class LodgeStaticTotals
{
    public static int Count { get; set; }        // static: no instance fact for an anchor to name
}

public sealed class LodgeNotifyingRow
{
    public string Name { get; set; } = "";
    public event Action? Changed;                 // a subscription surface, not data
}

public sealed class LodgeMoney
{
    public decimal Amount { get; set; }
    public static LodgeMoney operator +(LodgeMoney a, LodgeMoney b) => new() { Amount = a.Amount + b.Amount };
}

// A data-only base and a derived DTO that declares NOTHING of its own. It is still the carrier of
// that data, so it must be admitted — and its anchor inventory has to list the inherited property,
// or the conservation gate would prove facts against an empty path set and never notice one going
// missing.
public abstract class LodgeRateBase
{
    public string Band { get; set; } = "";
}

public sealed class LodgeSeasonalRateInfo : LodgeRateBase
{
}

// A plain data-carrying record. Every record implements IEquatable<T>, so it guards the boundary of
// the behavior check above: counting all interface members instead of only non-abstract ones would
// throw every record in the solution out of the DTO set.
public sealed record LodgeTariffRow(string Band, decimal Nightly);

public interface ILodgeArchivable { void Archive(); }

// One public property, no public methods on the class — but the explicit implementation below is
// `private` on the symbol while still being callable by anyone who casts to ILodgeArchivable. An
// accessibility filter alone reads this as a pure data carrier; hidden behavior is still behavior.
public sealed class LodgeArchiveRow : ILodgeArchivable
{
    public string Name { get; set; } = "";
    void ILodgeArchivable.Archive() { }
}

// The Contracts-side half of a PARTIAL DTO whose other half lives at the assembly root (see
// AmenityPartial.cs). Only one of the two source locations is the type's "primary" one, and which
// depends on syntax-tree order — so membership must be decided from ALL declarations, not from the
// primary location alone, or an unrelated file reordering silently changes the score.
public sealed partial class LodgeAmenityInfo
{
    public string Name { get; set; } = "";
}
