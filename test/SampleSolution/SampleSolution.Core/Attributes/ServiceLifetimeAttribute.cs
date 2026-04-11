namespace SampleSolution.Core.Attributes;

/// <summary>
/// Custom attribute for testing attribute-based references.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public class ServiceLifetimeAttribute : Attribute
{
    public string Lifetime { get; }

    public ServiceLifetimeAttribute(string lifetime)
    {
        Lifetime = lifetime;
    }
}
