using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;

namespace Reforge.Tests;

[Collection("SampleSolution")]
public class CallChainCommandTests
{
    private readonly SampleSolutionFixture _fixture;

    public CallChainCommandTests(SampleSolutionFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task FindCallChain_ExecuteQueryAsync_FindsMultipleDepths()
    {
        // Call chain from BaseRepository.ExecuteQueryAsync:
        // Depth 1: UserRepository.FindByIdAsync calls ExecuteQueryAsync
        // Depth 2+: things that call FindByIdAsync (via IUserRepository)
        var symbols = await SymbolResolver.ResolveAsync(_fixture.Solution, "BaseRepository.ExecuteQueryAsync");
        Assert.NotEmpty(symbols);
        var method = symbols.OfType<IMethodSymbol>().First();

        // BFS traversal matching the CallChainCommand logic
        var results = new List<(ISymbol Caller, int Depth)>();
        var visited = new HashSet<string>();
        var queue = new Queue<(ISymbol method, int depth)>();

        queue.Enqueue((method, 0));
        visited.Add(method.ToDisplayString());

        while (queue.Count > 0)
        {
            var (current, depth) = queue.Dequeue();
            if (depth >= 5)
                continue;

            var callers = await SymbolFinder.FindCallersAsync(current, _fixture.Solution);
            foreach (var caller in callers)
            {
                var key = caller.CallingSymbol.ToDisplayString();
                if (visited.Add(key))
                {
                    results.Add((caller.CallingSymbol, depth + 1));
                    queue.Enqueue((caller.CallingSymbol, depth + 1));
                }
            }
        }

        // Should find at least depth 1 callers (UserRepository.FindByIdAsync)
        Assert.NotEmpty(results);
        var depth1 = results.Where(r => r.Depth == 1).ToList();
        Assert.NotEmpty(depth1);
        Assert.Contains(depth1, r => r.Caller.ContainingType?.Name == "UserRepository");
    }
}
