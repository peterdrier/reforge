namespace Reforge;

/// <summary>
/// Structural facts about a section: it is RepoBacked when its assembly declares a repository or
/// a DbContext. Descriptive only — no rule expects a repo-backed section to publish any
/// particular interface or DTO.
/// </summary>
public sealed record SectionFacts(string Name, bool RepoBacked)
{
    /// <param name="repoBackedSections">
    /// Names of sections that own persistence: a repositoryInterface, a repositoryImplementation,
    /// or a DbContext — the caller building this set must include all three.
    /// </param>
    public static SectionFacts For(string name, IReadOnlySet<string> repoBackedSections)
        => new(name, repoBackedSections.Contains(name));
}
