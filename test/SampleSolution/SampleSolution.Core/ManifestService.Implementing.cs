namespace SampleSolution.Core;

/// <summary>The IMPLEMENTING half — this is where the body lives.</summary>
public partial class ManifestService
{
    public partial string GetManifest(int id) => $"manifest-{id}";
}
