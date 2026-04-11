using Microsoft.CodeAnalysis;

namespace Reforge.Tests;

[Collection("SampleSolution")]
public class DependenciesCommandTests
{
    private readonly SampleSolutionFixture _fixture;

    public DependenciesCommandTests(SampleSolutionFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task GetDependencies_UserController_FindsServiceDependencies()
    {
        var symbols = await SymbolResolver.ResolveAsync(
            _fixture.Solution, "SampleSolution.Web.Controllers.UserController");
        Assert.Single(symbols);
        var typeSymbol = (INamedTypeSymbol)symbols[0];

        var ctorParams = typeSymbol.Constructors
            .Where(c => !c.IsImplicitlyDeclared)
            .SelectMany(c => c.Parameters)
            .Select(p => p.Type.ToDisplayString())
            .ToList();

        Assert.Contains(ctorParams, t => t.Contains("IUserService"));
        Assert.Contains(ctorParams, t => t.Contains("INotificationService"));
    }

    [Fact]
    public async Task GetDependencies_UserService_FindsRepositoryAndLogger()
    {
        var symbols = await SymbolResolver.ResolveAsync(_fixture.Solution, "SampleSolution.Services.UserService");
        Assert.Single(symbols);
        var typeSymbol = (INamedTypeSymbol)symbols[0];

        var ctorParams = typeSymbol.Constructors
            .Where(c => !c.IsImplicitlyDeclared)
            .SelectMany(c => c.Parameters)
            .Select(p => p.Type.ToDisplayString())
            .ToList();

        Assert.Contains(ctorParams, t => t.Contains("IUserRepository"));
        Assert.Contains(ctorParams, t => t.Contains("ILogger"));
    }
}
