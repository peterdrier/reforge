using Microsoft.CodeAnalysis;

namespace Reforge.Tests;

[Collection("SampleSolution")]
public class SymbolResolverTests
{
    private readonly SampleSolutionFixture _fixture;

    public SymbolResolverTests(SampleSolutionFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ResolveSimpleName_FindsType()
    {
        var symbols = await SymbolResolver.ResolveAsync(_fixture.Solution, "UserService");
        Assert.NotEmpty(symbols);
        Assert.Contains(symbols, s => s is INamedTypeSymbol && s.Name == "UserService");
    }

    [Fact]
    public async Task ResolveQualifiedName_FindsExactType()
    {
        var symbols = await SymbolResolver.ResolveAsync(_fixture.Solution, "SampleSolution.Core.Models.User");
        Assert.Single(symbols);
        Assert.Equal("SampleSolution.Core.Models.User", symbols[0].ToDisplayString());
    }

    [Fact]
    public async Task ResolveMemberAccess_FindsMethod()
    {
        var symbols = await SymbolResolver.ResolveAsync(_fixture.Solution, "UserService.GetUserAsync");
        Assert.NotEmpty(symbols);
        Assert.Contains(symbols, s => s is IMethodSymbol && s.Name == "GetUserAsync");
    }

    [Fact]
    public async Task ResolveAmbiguousName_ReturnsMultiple()
    {
        var symbols = await SymbolResolver.ResolveAsync(_fixture.Solution, "User");
        // Should find at least Models.User and Dto.User
        var typeSymbols = symbols.OfType<INamedTypeSymbol>().ToList();
        Assert.True(typeSymbols.Count >= 2, $"Expected at least 2 type symbols named 'User', got {typeSymbols.Count}");
        Assert.Contains(typeSymbols, s => s.ToDisplayString().Contains("Models.User"));
        Assert.Contains(typeSymbols, s => s.ToDisplayString().Contains("Dto.User"));
    }

    [Fact]
    public async Task ResolveNonexistent_ReturnsEmpty()
    {
        var symbols = await SymbolResolver.ResolveAsync(_fixture.Solution, "NonexistentType");
        Assert.Empty(symbols);
    }
}
