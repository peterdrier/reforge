using System.Linq.Expressions;

namespace SampleSolution.Core.Data;

/// <summary>
/// Stub for testing — mimics EF Core's ModelBuilder fluent API.
/// </summary>
public class ModelBuilder
{
    public EntityTypeBuilder<T> Entity<T>() where T : class => new();
}

public class EntityTypeBuilder<T> where T : class
{
    public PropertyBuilder Property(string name) => new();
    public PropertyBuilder Property<TProp>(Expression<Func<T, TProp>> expr) => new();
}

public class PropertyBuilder
{
    public PropertyBuilder HasDefaultValue(object value) => this;
    public PropertyBuilder HasConversion<TConversion>() => this;
    public PropertyBuilder IsRequired() => this;
}
