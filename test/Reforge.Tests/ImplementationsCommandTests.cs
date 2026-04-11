using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;

namespace Reforge.Tests;

[Collection("SampleSolution")]
public class ImplementationsCommandTests
{
    private readonly SampleSolutionFixture _fixture;

    public ImplementationsCommandTests(SampleSolutionFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task FindImplementations_IUserService_FindsBothImplementations()
    {
        var symbols = await SymbolResolver.ResolveAsync(_fixture.Solution, "SampleSolution.Core.Interfaces.IUserService");
        Assert.Single(symbols);
        var typeSymbol = (INamedTypeSymbol)symbols[0];

        var implementations = await SymbolFinder.FindImplementationsAsync(typeSymbol, _fixture.Solution);
        var implNames = implementations.Select(i => i.Name).ToList();

        Assert.Contains("UserService", implNames);
        Assert.Contains("CachedUserService", implNames);
    }

    [Fact]
    public async Task FindImplementations_INotificationService_FindsNotificationService()
    {
        var symbols = await SymbolResolver.ResolveAsync(
            _fixture.Solution, "SampleSolution.Core.Interfaces.INotificationService");
        Assert.Single(symbols);
        var typeSymbol = (INamedTypeSymbol)symbols[0];

        var implementations = await SymbolFinder.FindImplementationsAsync(typeSymbol, _fixture.Solution);
        var implNames = implementations.Select(i => i.Name).ToList();

        Assert.Contains("NotificationService", implNames);
    }
}
