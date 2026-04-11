using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;

namespace Reforge.Tests;

[Collection("SampleSolution")]
public class InheritorsCommandTests
{
    private readonly SampleSolutionFixture _fixture;

    public InheritorsCommandTests(SampleSolutionFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task FindDerivedClasses_BaseEntity_FindsUserAndAuditLog()
    {
        var symbols = await SymbolResolver.ResolveAsync(_fixture.Solution, "SampleSolution.Core.Models.BaseEntity");
        Assert.Single(symbols);
        var typeSymbol = (INamedTypeSymbol)symbols[0];

        var derived = await SymbolFinder.FindDerivedClassesAsync(typeSymbol, _fixture.Solution);
        var derivedNames = derived.Select(d => d.Name).ToList();

        Assert.Contains("User", derivedNames);
        Assert.Contains("AuditLog", derivedNames);
    }

    [Fact]
    public async Task FindDerivedClasses_BaseRepository_FindsUserRepository()
    {
        var symbols = await SymbolResolver.ResolveAsync(_fixture.Solution, "SampleSolution.Services.Data.BaseRepository");
        Assert.Single(symbols);
        var typeSymbol = (INamedTypeSymbol)symbols[0];

        var derived = await SymbolFinder.FindDerivedClassesAsync(typeSymbol, _fixture.Solution);
        var derivedNames = derived.Select(d => d.Name).ToList();

        Assert.Contains("UserRepository", derivedNames);
    }
}
