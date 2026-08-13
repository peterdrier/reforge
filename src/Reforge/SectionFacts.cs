namespace Reforge;

/// <summary>
/// Resolved per-section architectural expectations. RepoBacked is derived from structure (the
/// section's assembly declares a repository or a DbContext); the requiresX flags default to
/// RepoBacked unless the section's policy overrides them.
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
    /// <param name="repoBackedSections">
    /// Names of sections that own persistence. Per the spec, a section is repo-backed if it
    /// declares EITHER a repositoryInterface, a repositoryImplementation, OR a DbContext — so the
    /// caller building this set must include all three, not just the interface-tagged groups.
    /// </param>
    public static SectionFacts For(string name, SectionRule policy, IReadOnlySet<string> repoBackedSections)
    {
        bool repoBacked = repoBackedSections.Contains(name);
        return new SectionFacts(
            name,
            repoBacked,
            policy.RequiresReadSurface ?? repoBacked,
            policy.RequiresWriteSurface ?? repoBacked,
            policy.RequiresPrimaryInfoDto ?? repoBacked);
    }
}
