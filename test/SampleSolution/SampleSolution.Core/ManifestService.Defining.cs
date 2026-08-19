using SampleSolution.Core.Interfaces;

namespace SampleSolution.Core;

/// <summary>
/// The DEFINING half of the partial implementation. Deliberately in its own file and listed first
/// alphabetically, so Roslyn is likely to enumerate this bodyless declaration before the one that
/// carries the body.
/// </summary>
public partial class ManifestService : IManifestService
{
    public partial string GetManifest(int id);
}
