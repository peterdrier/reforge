using SampleSolution.Camp.Contracts;
using SampleSolution.Core.Interfaces;
using SampleSolution.Core.Models;
using SampleSolution.Services;

namespace SampleSolution.Web;

/// <summary>
/// Fixtures for the <c>misplaced</c> command. One class per verdict, so a change to any single branch
/// moves one test rather than several. Each method's counts are chosen against the thresholds
/// deliberately: <c>MinimumTargetTouches</c> is 3, and the target must out-touch the method's own
/// section by <c>DominanceFactor</c> (2), so "3 calls out, 0 or 1 at home" is the smallest shape that
/// qualifies and is what most of these use.
/// </summary>
internal static class MisplacedFixtureNotes
{
}

/// <summary>
/// The plain pipe. Every touch is a call into <c>Services</c> behavior and none is its own, so the
/// method is doing Services' work from Web. The name is deliberately unique across the solution so
/// the duplicate check finds nothing.
/// </summary>
public class RelocatableGreetingReporter
{
    private readonly GreetingService _greetings = new();

    public async Task<string> SummarizeGreetingsForRelocation(int userId)
    {
        var greeting = await _greetings.GetGreetingAsync(userId);
        var recent = await _greetings.GetRecentGreetingsAsync(userId);
        await _greetings.RecordGreetingAsync(userId, greeting);
        return $"{greeting}/{recent.Count}";
    }
}

/// <summary>
/// The same pipe shape, but named for a method <c>Services</c> already declares
/// (<c>GreetingService.GetGreetingAsync</c>). Moving this verbatim would land a second
/// <c>GetGreetingAsync</c> in that section, which is the case a bare "move it there" recommendation
/// gets wrong.
/// </summary>
public class DuplicatingGreetingReporter
{
    private readonly GreetingService _greetings = new();

    // Signature matched to GreetingService.GetGreetingAsync deliberately, CancellationToken included:
    // an exact match is the decisive case, because the destination cannot compile with both.
    public async Task<string> GetGreetingAsync(int userId, CancellationToken cancellationToken = default)
    {
        var greeting = await _greetings.GetGreetingAsync(userId, cancellationToken);
        await _greetings.GetRecentGreetingsAsync(userId, cancellationToken);
        await _greetings.RecordGreetingAsync(userId, greeting, cancellationToken);
        return greeting;
    }
}

/// <summary>
/// A namesake rather than a collision: <c>Services</c> declares <c>GetGreetingAsync</c> too, but this
/// one takes an extra parameter, so both could live there at once. Still worth reporting before the
/// move — two methods of the same name doing nearly the same thing is how a section grows a confusing
/// API — but it is a weaker claim than an identical signature, and the evidence says which it is.
/// </summary>
public class NamesakeGreetingReporter
{
    private readonly GreetingService _greetings = new();

    public async Task<string> GetGreetingAsync(int userId, bool shout)
    {
        var greeting = await _greetings.GetGreetingAsync(userId);
        await _greetings.GetRecentGreetingsAsync(userId);
        await _greetings.RecordGreetingAsync(userId, greeting);
        return shout ? greeting.ToUpperInvariant() : greeting;
    }
}

/// <summary>
/// Null-safe delegation. Every call goes through <c>?.</c>, which puts the receiver under a
/// <c>ConditionalAccessExpression</c> and the invoked name under a member BINDING on the far side of the
/// operator — so a walk that only climbs member accesses never sees that <c>_greetings</c> is a receiver
/// and counts each one as own-section state. That restores the 1:1 tie the conduit rule exists to break,
/// which is the difference between this being reported and being invisible.
/// </summary>
public class NullSafeGreetingReporter
{
    private readonly GreetingService? _greetings = new();

    public async Task<string> SummarizeNullSafelyAsync(int userId)
    {
        var greeting = await (_greetings?.GetGreetingAsync(userId) ?? Task.FromResult(""));
        await (_greetings?.GetRecentGreetingsAsync(userId) ?? Task.FromResult<IReadOnlyList<string>>([]));
        await (_greetings?.RecordGreetingAsync(userId, greeting) ?? Task.CompletedTask);
        return greeting;
    }
}

/// <summary>
/// Named <c>PurgeAsync</c>, which <c>AuditLogQueryService</c> in the destination section also declares —
/// but this method leans on <see cref="GreetingService"/>, which declares no such thing. C# only forbids
/// duplicate signatures within one containing TYPE, so an unrelated namesake elsewhere in the same
/// assembly is not a collision and must not be reported as one. A section-wide name index could not tell
/// the two apart, and common method names made that a frequent false claim.
/// </summary>
public class UnrelatedNamesakeReporter
{
    private readonly GreetingService _greetings = new();

    public async Task<string> PurgeAsync(int userId)
    {
        var greeting = await _greetings.GetGreetingAsync(userId);
        await _greetings.RecordGreetingAsync(userId, greeting);
        await _greetings.RecordGreetingAsync(userId, greeting + "!");
        return greeting;
    }
}

/// <summary>
/// A DEFAULT interface method that pipes into another section. It is not bound by a contract — it IS one,
/// which neither the override nor the interface-implementation branch catches, since
/// <c>AllInterfaces</c> excludes the interface a member is declared on. Relocating this body alone changes
/// what every implementer inherits, so it must read as <c>blocked</c> rather than as a move.
/// </summary>
public interface IDefaultPipingReport
{
    async Task<string> SummarizeByDefaultAsync(int userId)
    {
        var service = new GreetingService();
        var greeting = await service.GetGreetingAsync(userId);
        await service.GetRecentGreetingsAsync(userId);
        await service.RecordGreetingAsync(userId, greeting);
        return greeting;
    }
}

/// <summary>
/// Same name and parameters as <c>GreetingService.GetRecentGreetingsAsync</c>, different return type. C#
/// does not allow overloading on return type, so this is a decisive collision — comparing return types
/// made the analyzer call it a near-miss and then report "different parameter types", which was both the
/// wrong verdict and a false reason for it.
/// </summary>
public class ReturnTypeClashReporter
{
    private readonly GreetingService _greetings = new();

    public async Task<int> GetRecentGreetingsAsync(int userId, CancellationToken cancellationToken = default)
    {
        var greeting = await _greetings.GetGreetingAsync(userId, cancellationToken);
        var recent = await _greetings.GetRecentGreetingsAsync(userId, cancellationToken);
        await _greetings.RecordGreetingAsync(userId, greeting, cancellationToken);
        return recent.Count;
    }
}

/// <summary>
/// Delegation written with the null-forgiving operator. <c>_dep!</c> is a transparent wrapper — it changes
/// nothing about what is reached — but a walk that does not climb it counts every receiver at home and
/// restores the 1:1 tie, exactly as the conditional-access case did.
/// </summary>
public class NullForgivingGreetingReporter
{
    private readonly GreetingService? _greetings = new();

    public async Task<string> SummarizeForgivinglyAsync(int userId)
    {
        var greeting = await _greetings!.GetGreetingAsync(userId);
        await _greetings!.GetRecentGreetingsAsync(userId);
        await _greetings!.RecordGreetingAsync(userId, greeting);
        return greeting;
    }
}

/// <summary>
/// Reaches three other sections — <c>Core</c>, <c>Services</c>, and <c>Camp</c> — so no single section
/// could host it. Nothing is misplaced here; the method exists to join sections. It is reported anyway
/// because an accidental junction drawer has exactly this shape.
/// </summary>
public class SectionJoiningDashboard
{
    private readonly IUserService _users;
    private readonly GreetingService _greetings = new();
    private readonly ICampServiceRead _camps;

    public SectionJoiningDashboard(IUserService users, ICampServiceRead camps)
    {
        _users = users;
        _camps = camps;
    }

    public async Task<string> BuildDashboardAsync(int userId, Guid campId)
    {
        var user = await _users.GetUserAsync(userId);
        var greeting = await _greetings.GetGreetingAsync(userId);
        var camp = await _camps.GetByIdAsync(campId);
        var settings = await _camps.GetSettingsAsync(campId);
        return $"{user?.Name}/{greeting}/{camp.Name}/{settings}";
    }
}

/// <summary>
/// Reads <c>Core</c>'s data carrier and calls none of its behavior. That is what mapping code looks
/// like from here, and a mapper belongs to whoever needs the mapped shape — so no move is proposed.
/// </summary>
public class UserRowMapper
{
    public string MapToRow(User user) => $"{user.Name}|{user.Email}|{user.IsActive}|{user.Id}";
}

/// <summary>
/// A pipe that cannot move on its own: the method implements <see cref="IRelocationReport"/>, so the
/// interface would have to move with it. That is a larger change than relocating a file, and the
/// distinction is the point — the finding is still true, but the fix is different.
/// </summary>
public class ContractBoundReporter : IRelocationReport
{
    private readonly GreetingService _greetings = new();

    public async Task<string> RenderAsync(int userId)
    {
        var greeting = await _greetings.GetGreetingAsync(userId);
        var recent = await _greetings.GetRecentGreetingsAsync(userId);
        await _greetings.RecordGreetingAsync(userId, greeting);
        return $"{greeting}:{recent.Count}";
    }
}

/// <summary>The contract that pins <see cref="ContractBoundReporter.RenderAsync"/> in place.</summary>
public interface IRelocationReport
{
    Task<string> RenderAsync(int userId);
}

/// <summary>
/// Below the threshold on purpose. Two calls into another section is what most delegating code in any
/// solution looks like, so this must NOT be reported — otherwise the command's output is every method
/// that calls a dependency twice.
/// </summary>
public class BarelyDelegatingReporter
{
    private readonly GreetingService _greetings = new();

    public Task<string> GetOneAsync(int userId)
    {
        _ = _greetings.GetRecentGreetingsAsync(userId);
        return _greetings.GetGreetingAsync(userId);
    }
}

/// <summary>
/// Above the touch threshold but not dominant: it works on its own section as much as on the other, so
/// it is not misplaced. Guards the <c>DominanceFactor</c> half of the test, which a touch count alone
/// would miss.
/// </summary>
public class BalancedReporter
{
    private readonly GreetingService _greetings = new();

    private string Own(string s) => s.Trim();
    private string OwnToo(string s) => s.ToUpperInvariant();
    private string OwnAgain(string s) => s.ToLowerInvariant();

    public async Task<string> BlendAsync(int userId)
    {
        var greeting = await _greetings.GetGreetingAsync(userId);
        var recent = await _greetings.GetRecentGreetingsAsync(userId);
        await _greetings.RecordGreetingAsync(userId, greeting);
        return Own(greeting) + OwnToo(greeting) + OwnAgain(greeting) + recent.Count;
    }
}

/// <summary>
/// The plain pipe again, written as the implementation half of a partial method. The finding is true
/// but the fix is not a relocation: C# requires both halves of a partial method in the same containing
/// type, so the body cannot travel to another type or assembly without its declaration.
/// </summary>
public partial class PartialPipingReporter
{
    private readonly GreetingService _greetings = new();

    public partial Task<string> SummarizePartiallyAsync(int userId);
}

public partial class PartialPipingReporter
{
    public partial async Task<string> SummarizePartiallyAsync(int userId)
    {
        var greeting = await _greetings.GetGreetingAsync(userId);
        var recent = await _greetings.GetRecentGreetingsAsync(userId);
        await _greetings.RecordGreetingAsync(userId, greeting);
        return $"{greeting}/{recent.Count}";
    }
}

/// <summary>
/// A pipe whose generic signature matches one the destination type already declares
/// (<c>GenericMappingService.Passthrough&lt;U&gt;(U)</c>). The two methods' type parameters are
/// distinct symbols, so identity comparison read this as a namesake with different parameter types
/// when it is a straight compile-time collision.
/// </summary>
public class GenericClashReporter
{
    private readonly GenericMappingService _mapping = new();

    public T Passthrough<T>(T value)
    {
        var first = _mapping.Passthrough(value);
        var second = _mapping.Passthrough(first);
        var third = _mapping.Passthrough(second);
        return third;
    }
}

/// <summary>
/// Reads four properties of a type the config classifies as a DTO but whose shape the structural test
/// rejects. Mapping code belongs to whoever needs the mapped shape, so this is a mapper — counting the
/// reads as behavior made it a move recommendation with a destination.
/// </summary>
public class ConfiguredDtoRowMapper
{
    public string MapConfiguredSummary(RelocationSummaryResult summary) =>
        $"{summary.Label}|{summary.Count}|{summary.Kind}|{summary.Note}";
}

/// <summary>
/// Calls three methods on a type the config labels a DTO. A config rule states a type's role, not the
/// shape of its members, so these are behavior calls and the method is doing the other section's work.
/// Classifying every touch on a configured DTO as data reported this as a mapper.
/// </summary>
public class ConfiguredDtoBehaviorCaller
{
    public string ShoutSummary(VerboseSummaryResult summary) =>
        $"{summary.Describe()}{summary.Shout()}{summary.Whisper()}";
}

/// <summary>
/// A method whose work sits in a local function. The touches are charged to the enclosing method
/// deliberately: a local function cannot be relocated on its own — it moves with the method that
/// declares it — so its calls are part of what moving that method would move.
/// </summary>
public class LocalFunctionReporter
{
    private readonly GreetingService _greetings = new();

    public async Task<string> SummarizeViaLocalFunctionAsync(int userId)
    {
        return await CollectAsync();

        async Task<string> CollectAsync()
        {
            var greeting = await _greetings.GetGreetingAsync(userId);
            var recent = await _greetings.GetRecentGreetingsAsync(userId);
            await _greetings.RecordGreetingAsync(userId, greeting);
            return $"{greeting}/{recent.Count}";
        }
    }
}

/// <summary>
/// A pipe whose namesake at the destination differs only in how a parameter is passed
/// (<c>ref</c> here, <c>out</c> there). C# cannot declare both, so this is a decisive collision.
/// </summary>
public class RefKindClashReporter
{
    private readonly RefKindTargetService _target = new();

    public bool TryPassthrough(ref string value)
    {
        value = _target.Echo(value);
        var again = _target.Echo(value);
        var third = _target.Echo(again);
        return third.Length > 0;
    }
}

/// <summary>
/// Reads three inherited properties of a configured DTO plus one it declares itself. All four are data
/// reads: the config rule names the type the caller is holding, and inheritance does not turn a property
/// into behavior.
/// </summary>
public class InheritedDtoRowMapper
{
    public string MapInheritedSummary(InheritedSummaryResult summary) =>
        $"{summary.Id}|{summary.Slug}|{summary.Id}|{summary.Label}";
}

/// <summary>
/// The delegating pipe again, declared on a base whose <b>derived</b> type is what binds it to an
/// interface. Asked from this class there is no contract to find, which is why the analyzer needs an
/// index built from every type rather than a lookup on the declaring one.
/// </summary>
public class InheritedContractReporterBase
{
    private readonly GreetingService _greetings = new();

    public async Task<string> RenderInheritedAsync(int userId)
    {
        var greeting = await _greetings.GetGreetingAsync(userId);
        var recent = await _greetings.GetRecentGreetingsAsync(userId);
        await _greetings.RecordGreetingAsync(userId, greeting);
        return $"{greeting}/{recent.Count}";
    }
}

public interface IInheritedContractReport
{
    Task<string> RenderInheritedAsync(int userId);
}

public class InheritedContractReporter : InheritedContractReporterBase, IInheritedContractReport
{
}

/// <summary>
/// A method whose foreign work is expressed entirely through <c>new</c>. Nothing is called on the other
/// section; three of its types are constructed. Counting only member accesses reported no finding at all.
/// </summary>
public class ConstructingReporter
{
    public string BuildWorkItems(string a, string b, string c)
    {
        var first = new ConstructedWorkItem(a);
        var second = new ConstructedWorkItem(b);
        var third = new ConstructedWorkItem(c);
        return $"{first}{second}{third}";
    }
}

/// <summary>
/// The inherited-contract case again, but supplied through a <b>constructed</b> base:
/// <c>Derived : Base&lt;int&gt;, IGenericContractReport</c> is served by <c>Base&lt;T&gt;.RenderGenericAsync(T)</c>.
/// The interface map resolves to the substituted <c>Base&lt;int&gt;.RenderGenericAsync(int)</c>, so an
/// index keyed on the substituted symbol never matches the method as declared.
/// </summary>
public class GenericContractReporterBase<T>
{
    private readonly GreetingService _greetings = new();

    public async Task<string> RenderGenericAsync(T key)
    {
        var greeting = await _greetings.GetGreetingAsync(1);
        var recent = await _greetings.GetRecentGreetingsAsync(1);
        await _greetings.RecordGreetingAsync(1, greeting);
        return $"{key}/{greeting}/{recent.Count}";
    }
}

public interface IGenericContractReport
{
    Task<string> RenderGenericAsync(int key);
}

public class GenericContractReporter : GenericContractReporterBase<int>, IGenericContractReport
{
}

/// <summary>
/// Switches over another section's enum. Every touch is an enum member — a constant, not a call — so
/// this is a mapper, and no destination TYPE is proposed: the only Services type it touches is an enum,
/// which cannot host a method.
/// </summary>
public class EnumMappingReporter
{
    public int SizeToSquareMetres(WorkItemSize size) => size switch
    {
        WorkItemSize.Small => 10,
        WorkItemSize.Medium => 20,
        WorkItemSize.Large => 40,
        _ => 80
    };
}

/// <summary>
/// Uses another section entirely through an indexer. The table arrives as a parameter, so there is no
/// receiver field to weigh at home — the three reads are the whole measurement.
/// </summary>
public class IndexerReadingReporter
{
    public string SummarizeBySlot(SlotTable table) => $"{table[0]}|{table[1]}|{table[2]}";
}

/// <summary>
/// Two overloads differing only in how the parameter is passed. A derived type binds the <c>ref</c> one
/// to <c>IRefOverloadContract</c>; the by-value one is pinned by nothing. Keyed without the ref kind,
/// the contract on the first also blocked the second.
/// </summary>
public class RefOverloadReporterBase
{
    private readonly GreetingService _greetings = new();

    public string HandleRefOverload(ref int value)
    {
        var greeting = _greetings.GetGreetingAsync(value).GetAwaiter().GetResult();
        var recent = _greetings.GetRecentGreetingsAsync(value).GetAwaiter().GetResult();
        _greetings.RecordGreetingAsync(value, greeting).GetAwaiter().GetResult();
        return $"{greeting}/{recent.Count}";
    }

    public string HandleRefOverload(int value)
    {
        var greeting = _greetings.GetGreetingAsync(value).GetAwaiter().GetResult();
        var recent = _greetings.GetRecentGreetingsAsync(value).GetAwaiter().GetResult();
        _greetings.RecordGreetingAsync(value, greeting).GetAwaiter().GetResult();
        return $"{greeting}/{recent.Count}";
    }
}

public class RefOverloadReporter : RefOverloadReporterBase, IRefOverloadContract
{
}

/// <summary>
/// Delegates through an indexer held in a field. The receiver is a conduit exactly as in
/// <c>_dep.Method()</c>: unrecognised, three reads scored 3 own against 3 target and tied, which is the
/// shape the conduit rule exists to break. The earlier indexer fixture took its table as a parameter and
/// so never exercised this path.
/// </summary>
public class HeldIndexerReporter
{
    private readonly SlotTable _table = new();

    public string SummarizeHeldSlots() => $"{_table[0]}|{_table[1]}|{_table[2]}";
}

/// <summary>
/// The same delegation through a null-safe indexer. <c>table?[0]</c> puts the indexer on an
/// ElementBindingExpression rather than an ElementAccessExpression — a separate node type.
/// </summary>
public class NullSafeIndexerReporter
{
    public string SummarizeNullSafeSlots(SlotTable? table) => $"{table?[0]}|{table?[1]}|{table?[2]}";
}

/// <summary>
/// A public base pinned by a <b>private</b> derived type. Nothing outside this file can see
/// <c>PrivateContractHolder</c>, and the pin is real regardless.
/// </summary>
public class PrivatelyPinnedReporterBase
{
    private readonly GreetingService _greetings = new();

    public string DescribePrivately(int value)
    {
        var greeting = _greetings.GetGreetingAsync(value).GetAwaiter().GetResult();
        var recent = _greetings.GetRecentGreetingsAsync(value).GetAwaiter().GetResult();
        _greetings.RecordGreetingAsync(value, greeting).GetAwaiter().GetResult();
        return $"{greeting}/{recent.Count}";
    }
}

internal static class PrivateContractHolder
{
    private sealed class Hidden : PrivatelyPinnedReporterBase, IPrivatelyImplementedContract
    {
    }

    internal static IPrivatelyImplementedContract Create() => new Hidden();
}

/// <summary>
/// The held-indexer delegation reached through <c>?[</c>. The receiver sits under a conditional access
/// and the indexer on an ElementBindingExpression, so a conduit walk that recognises only member
/// bindings scores <c>_table</c> as own state and the delegation ties instead of moving.
/// </summary>
public class NullSafeHeldIndexerReporter
{
    private readonly SlotTable? _table = new();

    public string SummarizeNullSafeHeldSlots() => $"{_table?[0]}|{_table?[1]}|{_table?[2]}";
}

/// <summary>
/// A dominant pipe declared on a generic type, using that type's parameter in its signature. No
/// destination can declare <c>T</c>, so this is a pin rather than a relocation.
/// </summary>
public class GenericSourceReporter<T>
{
    private readonly GreetingService _greetings = new();

    public string DescribeWithTypeParameter(T payload)
    {
        var greeting = _greetings.GetGreetingAsync(1).GetAwaiter().GetResult();
        var recent = _greetings.GetRecentGreetingsAsync(1).GetAwaiter().GetResult();
        _greetings.RecordGreetingAsync(1, greeting).GetAwaiter().GetResult();
        return $"{payload}:{greeting}/{recent.Count}";
    }
}

/// <summary>
/// Three reads through an indexer the configured DTO <i>inherits</i>. An element access carries its
/// receiver on the node itself rather than on its parent, so a receiver lookup written only for member
/// accesses saw nothing and judged the reads by the unconfigured declaring base.
/// </summary>
public class InheritedIndexerRowMapper
{
    public string SummarizeInheritedIndexedRow(IndexedSummaryResult row) => $"{row[0]}|{row[1]}|{row[2]}";

    public string SummarizeNullSafeInheritedIndexedRow(IndexedSummaryResult? row) =>
        $"{row?[0]}|{row?[1]}|{row?[2]}";
}

/// <summary>
/// Delegation through a cast receiver. The cast changes nothing about what is reached, but a wrapper
/// walk that stops at it counts the field as own state and the pipe ties 3:3.
/// </summary>
public class CastingConduitReporter
{
    private readonly object _greetings = new GreetingService();

    public string SummarizeThroughCast()
    {
        var greeting = ((GreetingService)_greetings).GetGreetingAsync(1).GetAwaiter().GetResult();
        var recent = ((GreetingService)_greetings).GetRecentGreetingsAsync(1).GetAwaiter().GetResult();
        ((GreetingService)_greetings).RecordGreetingAsync(1, greeting).GetAwaiter().GetResult();
        return $"{greeting}/{recent.Count}";
    }

    public string SummarizeThroughAsCast()
    {
        var greeting = (_greetings as GreetingService)!.GetGreetingAsync(1).GetAwaiter().GetResult();
        var recent = (_greetings as GreetingService)!.GetRecentGreetingsAsync(1).GetAwaiter().GetResult();
        (_greetings as GreetingService)!.RecordGreetingAsync(1, greeting).GetAwaiter().GetResult();
        return $"{greeting}/{recent.Count}";
    }
}

/// <summary>
/// Four calls into another section, none of them written as a member access: two additions, a negation
/// and an explicit conversion, all user-defined on <c>SlotWeight</c>.
/// </summary>
public class OperatorUsingReporter
{
    public int SumSlotWeights(SlotWeight a, SlotWeight b, SlotWeight c)
    {
        var total = a + b;
        total = total + c;
        var negated = -total;
        return (int)negated;
    }
}

/// <summary>
/// Two other sections, split evenly. Neither could host it without leaving the other reached from the
/// wrong side, which is the orchestrator argument at its smallest fan-out.
/// </summary>
public class TwoSectionJunctionReporter
{
    private readonly GreetingService _greetings = new();
    private readonly ICampServiceRead _camps;

    public TwoSectionJunctionReporter(ICampServiceRead camps) => _camps = camps;

    public async Task<string> SummarizeTwoSectionsAsync(Guid campId)
    {
        var greeting = await _greetings.GetGreetingAsync(1);
        var recent = await _greetings.GetRecentGreetingsAsync(1);
        var camp = await _camps.GetByIdAsync(campId);
        var settings = await _camps.GetSettingsAsync(campId);
        return $"{greeting}/{recent.Count}/{camp.Name}/{settings}";
    }
}

/// <summary>
/// Delegation through an awaited receiver. The field holds a task, so every call reaches the other
/// section through <c>await</c> — a wrapper that leaves the receiver exactly what it was.
/// </summary>
public class AwaitedConduitReporter
{
    private readonly Task<GreetingService> _greetings = Task.FromResult(new GreetingService());

    public async Task<string> SummarizeThroughAwaitAsync()
    {
        var greeting = await (await _greetings).GetGreetingAsync(1);
        var recent = await (await _greetings).GetRecentGreetingsAsync(1);
        await (await _greetings).RecordGreetingAsync(1, greeting);
        return $"{greeting}/{recent.Count}";
    }
}

/// <summary>
/// A virtual pipe with a derived override. Nothing on the base declaration says it is overridden —
/// <c>IsOverride</c> is false there — so it read as freely movable, and moving it would leave
/// <c>OverridingPipe</c> with nothing to override.
/// </summary>
public class OverriddenPipeBase
{
    private readonly GreetingService _greetings = new();

    public virtual string DescribeVirtually(int value)
    {
        var greeting = _greetings.GetGreetingAsync(value).GetAwaiter().GetResult();
        var recent = _greetings.GetRecentGreetingsAsync(value).GetAwaiter().GetResult();
        _greetings.RecordGreetingAsync(value, greeting).GetAwaiter().GetResult();
        return $"{greeting}/{recent.Count}";
    }
}

public class OverridingPipe : OverriddenPipeBase
{
    public override string DescribeVirtually(int value) => base.DescribeVirtually(value) + "!";
}

/// <summary>
/// Three reads of a configured DTO's <i>inherited</i> properties written as a property pattern. A
/// pattern name has no receiver expression beside it — the receiver is the pattern's input — so the
/// reads were judged against the unconfigured base that declares them.
/// </summary>
public class PatternReadingRowMapper
{
    public bool SummarizePatternedRow(PatternedSummaryResult row) =>
        row is { Id: > 0, Slug: not null, Code: not null };
}

/// <summary>
/// A dominant pipe whose namesake at the destination differs only in the name of a type parameter
/// inside a function-pointer parameter. C# calls that one signature.
/// </summary>
public class PointerPassthroughReporter
{
    private readonly PointerPassthroughService _pointers = new();

    public unsafe string PointerPassthrough<T>(delegate*<T, void> callback)
    {
        var first = _pointers.Describe(1);
        var second = _pointers.Describe(2);
        var third = _pointers.Describe(3);
        return callback is null ? first : $"{second}{third}";
    }
}
