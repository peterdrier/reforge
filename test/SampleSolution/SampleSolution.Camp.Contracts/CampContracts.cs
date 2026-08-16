namespace SampleSolution.Camp.Contracts;

// The Camp section's published contracts. This assembly is `SampleSolution.Camp.Contracts`, so
// grouping must FOLD it into the `Camp` section — the read interface and the primary Info DTO
// below have to land in the same section as ICampSectionService over in SampleSolution.Camp.

public interface ICampServiceRead
{
    Task<CampInfo> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<CampSettingsInfo> GetSettingsAsync(Guid campId, CancellationToken ct = default);
    Task<bool> IsUserCampLeadAsync(Guid campId, Guid userId, CancellationToken ct = default);            // predicate (charged)
    Task<List<CampSummary>> GetCampSummariesForYearAsync(int year, CancellationToken ct = default);      // projection (charged)
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

// Named like a domain type but exported from the section's contracts assembly, so it IS Camp's
// published read API and returning it across a section boundary is credited. Its twin
// CampLegacyEntity lives in SampleSolution.Camp, off the contracts surface, and earns nothing.
// The pair exists to prove the derivation reads location and export, never the name.
public sealed class CampStayEntity
{
    public Guid Id { get; set; }
    public int Nights { get; set; }
}
