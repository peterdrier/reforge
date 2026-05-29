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
    /// <summary>
    /// Resolves the architectural expectations for a single section.
    /// </summary>
    /// <param name="classifiedRepoSectionNames">
    /// Names of sections that are repo-backed by classification. Per the spec, a section is
    /// repo-backed if it owns EITHER a repositoryInterface OR a repositoryImplementation, so the
    /// caller building this set must include section names tagged with either — not just the
    /// interface-tagged groups.
    /// </param>
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
