using Microsoft.CodeAnalysis;

namespace Reforge.Tests;

[Collection("SampleSolution")]
public class InjectedCommandTests
{
    private readonly SampleSolutionFixture _fixture;

    public InjectedCommandTests(SampleSolutionFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task FindInjectors_IUserService_FindsThreeClasses()
    {
        // IUserService is injected into: CachedUserService, OrderService, UserController
        var symbols = await SymbolResolver.ResolveAsync(
            _fixture.Solution, "SampleSolution.Core.Interfaces.IUserService");
        Assert.Single(symbols);
        var targetType = (INamedTypeSymbol)symbols[0];
        var targetDisplayName = targetType.ToDisplayString();

        var injectors = new List<string>();

        foreach (var project in _fixture.Solution.Projects)
        {
            var compilation = await project.GetCompilationAsync();
            if (compilation is null) continue;

            foreach (var type in GetAllTypes(compilation.GlobalNamespace))
            {
                foreach (var ctor in type.Constructors)
                {
                    if (ctor.IsImplicitlyDeclared) continue;

                    foreach (var param in ctor.Parameters)
                    {
                        if (param.Type.ToDisplayString() == targetDisplayName)
                        {
                            injectors.Add(type.Name);
                        }
                    }
                }
            }
        }

        var distinct = injectors.Distinct().ToList();
        Assert.Contains("CachedUserService", distinct);
        Assert.Contains("OrderService", distinct);
        Assert.Contains("UserController", distinct);
        Assert.True(distinct.Count >= 3, $"Expected at least 3 injectors, got {distinct.Count}: {string.Join(", ", distinct)}");
    }

    private static IEnumerable<INamedTypeSymbol> GetAllTypes(INamespaceSymbol ns)
    {
        foreach (var member in ns.GetMembers())
        {
            if (member is INamespaceSymbol childNs)
            {
                foreach (var type in GetAllTypes(childNs))
                    yield return type;
            }
            else if (member is INamedTypeSymbol type)
            {
                yield return type;
            }
        }
    }
}
