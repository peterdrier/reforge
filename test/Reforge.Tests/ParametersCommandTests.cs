using Microsoft.CodeAnalysis;

namespace Reforge.Tests;

[Collection("SampleSolution")]
public class ParametersCommandTests
{
    private readonly SampleSolutionFixture _fixture;

    public ParametersCommandTests(SampleSolutionFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task FindParameters_NameIsAdmin_FindsDesignViolations()
    {
        // Search for parameters named "isAdmin" - should find in UserService and BadController
        var results = await FindParametersAsync(namePattern: "isAdmin", typePattern: null);

        Assert.True(results.Count >= 2,
            $"Expected at least 2 isAdmin parameters, got {results.Count}: {string.Join(", ", results.Select(r => r.ContainingType))}");

        var containingTypes = results.Select(r => r.ContainingType).Distinct().ToList();
        Assert.Contains("UserService", containingTypes);
        Assert.Contains("BadController", containingTypes);
    }

    [Fact]
    public async Task FindParameters_TypeCancellationToken_FindsMany()
    {
        // Many async methods have CancellationToken parameters
        var results = await FindParametersAsync(namePattern: null, typePattern: "CancellationToken");

        // There should be many CancellationToken params across the solution
        Assert.True(results.Count >= 5,
            $"Expected at least 5 CancellationToken parameters, got {results.Count}");
    }

    /// <summary>
    /// Mirrors the parameter search logic from ParametersCommand.
    /// </summary>
    private async Task<List<(string ContainingType, string MethodName, string ParamName, string ParamType)>> FindParametersAsync(
        string? namePattern, string? typePattern)
    {
        var results = new List<(string ContainingType, string MethodName, string ParamName, string ParamType)>();

        foreach (var project in _fixture.Solution.Projects)
        {
            var compilation = await project.GetCompilationAsync();
            if (compilation is null) continue;

            foreach (var type in GetAllTypes(compilation.GlobalNamespace))
            {
                foreach (var member in type.GetMembers().OfType<IMethodSymbol>())
                {
                    if (member.IsImplicitlyDeclared) continue;

                    foreach (var param in member.Parameters)
                    {
                        bool nameMatch = namePattern is null ||
                            param.Name.Contains(namePattern, StringComparison.OrdinalIgnoreCase);
                        bool typeMatch = typePattern is null ||
                            param.Type.ToDisplayString().Contains(typePattern, StringComparison.OrdinalIgnoreCase);

                        if (nameMatch && typeMatch)
                        {
                            results.Add((type.Name, member.Name, param.Name, param.Type.ToDisplayString()));
                        }
                    }
                }
            }
        }

        return results;
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
