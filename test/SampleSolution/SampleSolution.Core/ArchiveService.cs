using SampleSolution.Core.Interfaces;

namespace SampleSolution.Core;

/// <summary>Concrete implementation for the inherited-write-members fixture.</summary>
public class ArchiveService : IArchiveService
{
    private readonly List<string> _archived = new();

    public string GetArchivedName(int id) => _archived[id];

    public void Persist(string value) => _archived.Add(value);
}

/// <summary>Concrete implementation for the settable-property fixture. Nothing here writes.</summary>
public class RetentionService : IRetentionService
{
    public int RetentionDays { get; set; } = 30;

    public string GetPolicyName() => $"retain-{RetentionDays}";
}

/// <summary>
/// The only in-solution implementer of <see cref="ILedgerService"/>, and it implements nothing: the
/// member stays abstract, so the classifier observes no behavior at all.
/// </summary>
public abstract class LedgerServiceBase : ILedgerService
{
    public abstract string GetLedgerName(int id);
}

/// <summary>
/// Implements <see cref="IQuotaService"/> with a getter that commits. Pathological on purpose: the
/// point is that only the body says so.
/// </summary>
public class QuotaService : IQuotaService
{
    private readonly AuditDbContext _db = new();

    public int CurrentQuota
    {
        get
        {
            _db.SaveChanges();
            return 7;
        }
    }
}

/// <summary>Minimal stub so the getter above has something to commit on.</summary>
public class AuditDbContext
{
    public int SaveChanges() => 0;
}

/// <summary>
/// Publishes <see cref="ILookupService"/> without publishing a type. The implementation is private
/// and nested, so it never reaches the scored corpus.
/// </summary>
public static class LookupFactory
{
    public static ILookupService Create() => new Impl();

    private sealed class Impl : ILookupService
    {
        public string GetLabel(int id) => id.ToString();
    }
}

/// <summary>
/// Holds the <see cref="IBadgeService"/> implementation two levels deep. `EnumerateTypes` used to
/// yield a top-level type and its immediate children only, so `Inner.Impl` was invisible.
/// </summary>
public static class BadgeHost
{
    public static IBadgeService Create() => new Inner.Impl();

    public static class Inner
    {
        public sealed class Impl : IBadgeService
        {
            public string GetBadge(int id) => $"badge-{id}";
        }
    }
}

/// <summary>
/// Declares <see cref="IRosterService"/> and implements none of it. Abstract implementers are exempt
/// from the completeness requirement — they are partial implementations whose gaps their derived
/// classes fill, and each of those is checked in its own right.
/// </summary>
public abstract class RosterServiceBase : IRosterService
{
    public abstract string GetRosterName(int id);
}

/// <summary>The concrete implementer that actually accounts for the surface, read-only.</summary>
public class SeasonRosterService : RosterServiceBase
{
    public override string GetRosterName(int id) => $"roster-{id}";
}

/// <summary>Implements the writable-ref fixtures. Nothing in either body commits.</summary>
public class GaugeService : IGaugeService, ISlotService
{
    private int _current;
    private readonly int[] _slots = new int[8];

    public ref int Current => ref _current;

    public ref int GetSlot(int index) => ref _slots[index];
}

/// <summary>Implements the <c>ref readonly</c> negative control.</summary>
public class ReadingService : IReadingService
{
    private readonly int _value;
    private readonly int[] _readings = new int[8];

    public ref readonly int Value => ref _value;

    public ref readonly int GetReading(int index) => ref _readings[index];
}

/// <summary>Read-only implementers of the interface-field and non-public-member fixtures.</summary>
public class StateService : IStateService
{
    public string GetState() => "steady";
}

/// <summary>Implements the readonly/const negative control.</summary>
public class SettledService : ISettledService
{
    public string GetSetting() => "settled";
}

/// <summary>Implements the private-helper fixture; only the public member is implementable.</summary>
public class DigestService : IDigestService
{
    public string GetDigest(int id) => $"digest-{id}";
}

/// <summary>
/// Fixture for effective accessibility. <c>Hidden</c> is <c>private</c>, so <c>Hidden.Exposed</c> is
/// private in every sense that matters despite its own modifier — the recursive walk is what first made
/// it reachable, and it must not enter the scored corpus.
/// </summary>
public static class VisibilityHost
{
    private static class Hidden
    {
        public sealed class Exposed
        {
            public int Value => 1;
        }
    }
}

/// <summary>
/// Implements <see cref="IShelfService"/> with an arrow-bodied indexer getter that commits. The arrow
/// is the point: a block-bodied accessor was already read correctly.
/// </summary>
public class ShelfService : IShelfService
{
    private readonly AuditDbContext _db = new();

    public int this[int slot] => _db.SaveChanges() + slot;
}

/// <summary>Implements <see cref="IRackService"/> with an arrow-bodied indexer getter that reads.</summary>
public class RackService : IRackService
{
    private readonly int[] _slots = new int[4];

    public int this[int slot] => _slots[slot];
}

/// <summary>Implements <see cref="IStampService"/>'s static abstract command with a body that commits.</summary>
public class StampService : IStampService
{
    private static readonly AuditDbContext Db = new();

    public static void Stamp(int id) => Db.SaveChanges();
}

/// <summary>Implements <see cref="IPollService"/>'s static abstract member with a body that reads.</summary>
public class PollService : IPollService
{
    public static int Poll(int id) => id;
}

/// <summary>Implements <see cref="IVaultService"/>, leaving the default property alone.</summary>
public class VaultService : IVaultService
{
    public string GetVaultName(int id) => $"vault-{id}";
}

/// <summary>Implements <see cref="IPurgeService"/> — nothing static to supply.</summary>
public class PurgeService : IPurgeService
{
    public string GetPurgeState(int id) => $"purge-{id}";
}

/// <summary>Implements <see cref="ITallyService"/> — nothing static to supply.</summary>
public class TallyService : ITallyService
{
    public string GetTallyState(int id) => $"tally-{id}";
}

/// <summary>Backing for <see cref="IMeterService"/>'s static getter — an interface cannot hold state.</summary>
public static class MeterBacking
{
    public static readonly AuditDbContext Db = new();
}

/// <summary>Implements <see cref="IMeterService"/>'s instance member; the static getter is the interface's own.</summary>
public class MeterService : IMeterService
{
    public string GetMeterLabel(int id) => $"meter-{id}";
}

/// <summary>Implements <see cref="IDialService"/>'s instance member.</summary>
public class DialService : IDialService
{
    public string GetDialLabel(int id) => $"dial-{id}";
}

/// <summary>Replaces <see cref="IBeaconService"/>'s static virtual default with one that commits.</summary>
public class BeaconService : IBeaconService
{
    public static int Ping() => MeterBacking.Db.SaveChanges();
}

/// <summary>Replaces <see cref="IChimeService"/>'s static virtual default with one that reads.</summary>
public class ChimeService : IChimeService
{
    public static int Ping() => 1;
}

/// <summary>Implements <see cref="IFuseService"/>'s instance member; the field is the interface's own.</summary>
public class FuseService : IFuseService
{
    public string GetFuseLabel(int id) => $"fuse-{id}";
}
