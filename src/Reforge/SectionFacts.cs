namespace Reforge;

/// <summary>
/// Resolved per-section architectural expectations. RepoBacked is inferred (config repository
/// OR a classified repository resolved into the section); the requiresX flags default to
/// RepoBacked unless the config overrides them.
/// </summary>
public sealed record SectionFacts(
    string Name,
    bool RepoBacked,
    bool RequiresReadSurface,
    bool RequiresWriteSurface,
    bool RequiresPrimaryInfoDto)
{
    public static SectionFacts For(SectionRule rule, IReadOnlySet<string> classifiedRepoSectionNames)
    {
        bool repoBacked = rule.HasConfiguredRepository
            || classifiedRepoSectionNames.Contains(rule.Name);
        return new SectionFacts(
            rule.Name,
            repoBacked,
            rule.RequiresReadSurface ?? repoBacked,
            rule.RequiresWriteSurface ?? repoBacked,
            rule.RequiresPrimaryInfoDto ?? repoBacked);
    }
}
