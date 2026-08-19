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
