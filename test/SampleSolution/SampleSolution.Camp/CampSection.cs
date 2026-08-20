using SampleSolution.Camp.Contracts;

namespace SampleSolution.Camp;

// Section-architecture fixtures: a repo-backed section whose read surface + canonical Info DTO
// live in the sibling .Contracts assembly.

// Repository so the Camp section is repo-backed (drives requiresX defaults).
public interface ICampRepository
{
    Task<CampInfo?> FindAsync(Guid id, CancellationToken ct = default);
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

// Same shape as CampStaySummary over in SampleSolution.Camp.Contracts, but declared in the
// section's own assembly with no Contracts/ folder above it — off the contracts surface, so it is
// NOT a canonical read DTO and returning it earns no credit. The negative half of the derivation.
public sealed class CampLegacyStay
{
    public Guid Id { get; set; }
    public int Nights { get; set; }
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

// Base for a DTO declared in ANOTHER section (Reporting). The base-chain walk has to cross the
// project boundary to charge the derived type for what it publishes — the solution boundary is
// assembly membership, not whether the symbol happens to arrive with a source location.
public class CrossProjectEnvelopeBase
{
    public Guid Id { get; set; }
    public string Origin { get; set; } = "";
}

// Base for the boundary tests in InheritedDtoFixtures: DATA declared outside the deriving type's
// own solution is not that type's published shape, while BEHAVIOUR declared outside it is still
// callable on the type. Only one of the two crosses, so the pair needs a data-only base and a
// behavioural one, both declared in a section other than the deriving type's.
public class CrossProjectShapeBase
{
    public Guid Key { get; set; }
    public string Label { get; set; } = "";
}

public class CrossProjectBehaviourBase
{
    public Guid Key { get; set; }
    public string Describe() => "";
}

// Property type for the cross-project nested-DTO test: a solution type declared in a different
// section from the DTO that references it.
public sealed class CrossProjectNestedPayload
{
    public string Value { get; set; } = "";
}
