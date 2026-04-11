using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;

namespace Reforge.Tests;

[Collection("SampleSolution")]
public class CallersCommandTests
{
    private readonly SampleSolutionFixture _fixture;

    public CallersCommandTests(SampleSolutionFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task FindCallers_GetUserAsync_FindsControllerAndServiceCallers()
    {
        // IUserService.GetUserAsync is called by UserController and OrderService
        var symbols = await SymbolResolver.ResolveAsync(_fixture.Solution, "IUserService.GetUserAsync");
        Assert.NotEmpty(symbols);
        var method = symbols.OfType<IMethodSymbol>().First();

        var callers = await SymbolFinder.FindCallersAsync(method, _fixture.Solution);
        var callerNames = callers.Select(c => c.CallingSymbol.ContainingType?.Name ?? c.CallingSymbol.Name).ToList();

        // UserController calls GetUserAsync in GetUser, NotifyUser
        Assert.Contains(callerNames, n => n == "UserController");
        // OrderService calls GetUserAsync in GetOrderForUserAsync and SearchOrdersAsync
        Assert.Contains(callerNames, n => n == "OrderService");
    }

    [Fact]
    public async Task FindCallers_ExecuteQueryAsync_FindsRepositoryCaller()
    {
        // BaseRepository.ExecuteQueryAsync is called by UserRepository.FindByIdAsync
        var symbols = await SymbolResolver.ResolveAsync(_fixture.Solution, "BaseRepository.ExecuteQueryAsync");
        Assert.NotEmpty(symbols);
        var method = symbols.OfType<IMethodSymbol>().First();

        var callers = await SymbolFinder.FindCallersAsync(method, _fixture.Solution);
        var callerNames = callers.Select(c => c.CallingSymbol.ContainingType?.Name ?? c.CallingSymbol.Name).ToList();

        Assert.Contains(callerNames, n => n == "UserRepository");
    }
}
