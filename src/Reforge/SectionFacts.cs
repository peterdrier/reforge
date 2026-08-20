namespace Reforge;

/// <summary>
/// Resolved per-section architectural expectations, all derived from structure: a section is
/// RepoBacked when its assembly declares a repository or a DbContext, and that alone decides which
/// surfaces it is expected to publish.
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
    public static SectionFacts For(string name, IReadOnlySet<string> repoBackedSections)
    {
        bool repoBacked = repoBackedSections.Contains(name);
        return new SectionFacts(name, repoBacked, repoBacked, repoBacked, repoBacked);
    }
}
