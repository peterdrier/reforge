namespace SampleSolution.Services;

// Fixtures for the boundary-input scoring rules. The "bad" shapes mirror the Humans Camps
// parameter-bag refactor that gamed methodParameterOverflow.

public interface ICampService
{
    Task CreateCampAsync(CampRegistrationInput input, CancellationToken ct = default);
}

public sealed class CampService : ICampService
{
    public Task CreateCampAsync(CampRegistrationInput input, CancellationToken ct = default) => Task.CompletedTask;

    // Inline parameter-object construction: the same argument bundle, now built at the call site.
    public Task RegisterAsync(Guid userId, string name, string email, string phone, bool isSwiss, int year)
        => CreateCampAsync(new CampRegistrationInput(userId, name, email, phone, isSwiss, year));
}

/// <summary>
/// BAD: a public boundary input that hides all its state behind internal getters and adds no
/// behavior — a long signature folded into an object. Expected: publicInputWithHiddenState
/// AND parameterBagInput; and inlineParameterObjectConstruction at the call site above.
/// </summary>
public sealed class CampRegistrationInput
{
    public CampRegistrationInput(Guid createdByUserId, string name, string email, string phone, bool isSwiss, int year)
    {
        CreatedByUserId = createdByUserId;
        Name = name;
        Email = email;
        Phone = phone;
        IsSwiss = isSwiss;
        Year = year;
    }

    internal Guid CreatedByUserId { get; }
    internal string Name { get; }
    internal string Email { get; }
    internal string Phone { get; }
    internal bool IsSwiss { get; }
    internal int Year { get; }
}

public interface ICampRequestService
{
    Task SubmitAsync(CampRegistrationRequest request, CancellationToken ct = default);
}

public sealed class CampRequestService : ICampRequestService
{
    public Task SubmitAsync(CampRegistrationRequest request, CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>
/// GOOD: public readable state and real validation behavior. Must NOT be penalized by the
/// boundary-input rules even though its name ends in "Request" and it has several members.
/// </summary>
public sealed record CampRegistrationRequest(
    Guid CreatedByUserId,
    string Name,
    string ContactEmail,
    string ContactPhone,
    int Year)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name))
            throw new ArgumentException("Name is required.", nameof(Name));
    }
}

// --- Section-architecture fixtures (read/full pair + nested canonical Info DTO) ---

// Repository so the Camp section is repo-backed (drives requiresX defaults).
public interface ICampRepository
{
    Task<CampInfo?> FindAsync(Guid id, CancellationToken ct = default);
}

public interface ICampServiceRead
{
    Task<CampInfo> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<CampSettingsInfo> GetSettingsAsync(Guid campId, CancellationToken ct = default);
    Task<bool> IsUserCampLeadAsync(Guid campId, Guid userId, CancellationToken ct = default);            // predicate (charged)
    Task<List<CampSummary>> GetCampSummariesForYearAsync(int year, CancellationToken ct = default);      // projection (charged)
}

public interface ICampSectionService : ICampServiceRead
{
    Task RenameAsync(Guid id, string name, CancellationToken ct = default);
}

public sealed class CampSectionService : ICampSectionService
{
    public Task<CampInfo> GetByIdAsync(Guid id, CancellationToken ct = default) => Task.FromResult(new CampInfo());
    public Task<CampSettingsInfo> GetSettingsAsync(Guid campId, CancellationToken ct = default) => Task.FromResult(new CampSettingsInfo());
    public Task<bool> IsUserCampLeadAsync(Guid campId, Guid userId, CancellationToken ct = default) => Task.FromResult(false);
    public Task<List<CampSummary>> GetCampSummariesForYearAsync(int year, CancellationToken ct = default) => Task.FromResult(new List<CampSummary>());
    public Task RenameAsync(Guid id, string name, CancellationToken ct = default) => Task.CompletedTask;
}

public sealed class CampInfo
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public List<CampSeasonInfo> Seasons { get; set; } = new();
    public CampSeasonInfo? CurrentSeason { get; set; }
    public List<string> ImageUrls { get; set; } = new();
}

public sealed class CampSeasonInfo
{
    public int Year { get; set; }
    public List<CampMemberInfo> Members { get; set; } = new();
}

public sealed class CampMemberInfo
{
    public Guid UserId { get; set; }
    public bool IsLead { get; set; }
}

public sealed class CampSettingsInfo
{
    public int CurrentYear { get; set; }
    public DateTime NameLockDate { get; set; }
}

public sealed class CampSummary
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
}

// --- Missing-surface fixtures (repo-backed sections each lacking one expected surface) ---

// Repo-backed section missing a READ interface (has repo + full write service, no *ServiceRead).
public interface ILodgeRepository { Task<LodgeInfo?> FindAsync(Guid id, CancellationToken ct = default); }
public interface ILodgeService { Task RenameAsync(Guid id, string name, CancellationToken ct = default); }
public sealed class LodgeService : ILodgeService { public Task RenameAsync(Guid id, string name, CancellationToken ct = default) => Task.CompletedTask; }
public sealed class LodgeInfo { public Guid Id { get; set; } public string Name { get; set; } = ""; }

// Repo-backed section missing a WRITE/full interface (has repo + read, no full service).
public interface IDormRepository { Task<DormInfo?> FindAsync(Guid id, CancellationToken ct = default); }
public interface IDormServiceRead { Task<DormInfo> GetByIdAsync(Guid id, CancellationToken ct = default); }
public sealed class DormInfo { public Guid Id { get; set; } public string Name { get; set; } = ""; }

// Repo-backed section missing a primary Info DTO (has repo + read + full, no TentInfo).
public interface ITentRepository { Task FindAsync(Guid id, CancellationToken ct = default); }
public interface ITentServiceRead { Task<bool> ExistsAsync(Guid id, CancellationToken ct = default); }
public interface ITentService { Task PitchAsync(Guid id, CancellationToken ct = default); }
public sealed class TentService : ITentService { public Task PitchAsync(Guid id, CancellationToken ct = default) => Task.CompletedTask; }

// Orchestrator-only section (NO repository) - must NOT trip any missing* rule.
public interface IBookingOrchestrator { Task RunAsync(CancellationToken ct = default); }
public sealed class BookingOrchestrator : IBookingOrchestrator
{
    private readonly ICampSectionService _camp;
    public BookingOrchestrator(ICampSectionService camp) => _camp = camp;
    public Task RunAsync(CancellationToken ct = default) => Task.CompletedTask;
}

// --- Cross-section write-surface fixtures (consumer injects Camp's full interface) ---

// Cross-section caller that injects the Camp full interface but only READS -> crossSectionWriteSurface.
public sealed class CampReportBuilder
{
    private readonly ICampSectionService _camp;
    public CampReportBuilder(ICampSectionService camp) => _camp = camp;
    public async Task<string> BuildAsync(Guid id)
    {
        var info = await _camp.GetByIdAsync(id);   // read-covered (exists on ICampServiceRead)
        return info.Name;
    }
}

// Cross-section caller that PASSES the injected dependency onward -> unknown usage (advisory, not penalty).
public sealed class CampDelegator
{
    private readonly ICampSectionService _camp;
    public CampDelegator(ICampSectionService camp) => _camp = camp;
    public Task HandOffAsync() => Consume(_camp);            // escapes: passed as an argument
    private static Task Consume(ICampServiceRead svc) => Task.CompletedTask;
}

// --- Cache-inference fixture: a caching decorator whose cache value is its OWN DTO (not CampInfo) ---

public sealed class CampCacheEntry { public Guid Id { get; set; } public string Name { get; set; } = ""; }

public sealed class CachedCampReadService : ICampServiceRead
{
    private readonly Dictionary<Guid, CampCacheEntry> _cache = new();
    private readonly ICampServiceRead _inner;
    public CachedCampReadService(ICampServiceRead inner) => _inner = inner;
    public Task<CampInfo> GetByIdAsync(Guid id, CancellationToken ct = default) => _inner.GetByIdAsync(id, ct);
    public Task<CampSettingsInfo> GetSettingsAsync(Guid campId, CancellationToken ct = default) => _inner.GetSettingsAsync(campId, ct);
    public Task<bool> IsUserCampLeadAsync(Guid campId, Guid userId, CancellationToken ct = default) => _inner.IsUserCampLeadAsync(campId, userId, ct);
    public Task<List<CampSummary>> GetCampSummariesForYearAsync(int year, CancellationToken ct = default) => _inner.GetCampSummariesForYearAsync(year, ct);
}

// --- Conservation-gate fixture: a static stateless helper (a helper-extraction sink) ---

public static class CampReadModelProjection
{
    public static string BuildCampDetail(CampInfo info) => info.Name;
    public static bool IsUserCampLead(CampInfo info, Guid userId) => false;
}
