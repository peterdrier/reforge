using Microsoft.CodeAnalysis;

namespace Reforge.Tests;

[Collection("SampleSolution")]
public class MembersCommandTests
{
    private readonly SampleSolutionFixture _fixture;

    public MembersCommandTests(SampleSolutionFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task GetMembers_UserService_IncludesAllMemberKinds()
    {
        var symbols = await SymbolResolver.ResolveAsync(_fixture.Solution, "SampleSolution.Services.UserService");
        Assert.Single(symbols);
        var typeSymbol = (INamedTypeSymbol)symbols[0];

        var members = typeSymbol.GetMembers()
            .Where(m => !m.IsImplicitlyDeclared)
            .Where(m => m.Locations.Length > 0 && m.Locations[0].IsInSource)
            .ToList();

        // Should have fields (_userRepository, _logger, _instanceCount)
        var fields = members.OfType<IFieldSymbol>().ToList();
        Assert.True(fields.Count >= 3, $"Expected at least 3 fields, got {fields.Count}");

        // Should have properties (IsInitialized)
        var properties = members.OfType<IPropertySymbol>().ToList();
        Assert.True(properties.Count >= 1, $"Expected at least 1 property, got {properties.Count}");

        // Should have methods (GetUserAsync, GetAllUsersAsync, OnUserLoaded, GetInstanceCount, FindUserByNameAsync, GetUserWithPrivilegeCheckAsync)
        var methods = members.OfType<IMethodSymbol>()
            .Where(m => m.MethodKind == MethodKind.Ordinary)
            .ToList();
        Assert.True(methods.Count >= 5, $"Expected at least 5 methods, got {methods.Count}");

        // Should have constructor
        var ctors = members.OfType<IMethodSymbol>()
            .Where(m => m.MethodKind == MethodKind.Constructor)
            .ToList();
        Assert.NotEmpty(ctors);
    }

    [Fact]
    public async Task GetMembers_UserService_ExcludesImplicitMembers()
    {
        var symbols = await SymbolResolver.ResolveAsync(_fixture.Solution, "SampleSolution.Services.UserService");
        Assert.Single(symbols);
        var typeSymbol = (INamedTypeSymbol)symbols[0];

        var allMembers = typeSymbol.GetMembers().ToList();
        var explicitMembers = allMembers
            .Where(m => !m.IsImplicitlyDeclared)
            .Where(m => m.Locations.Length > 0 && m.Locations[0].IsInSource)
            .ToList();

        // Implicit members (like property backing fields, default constructors) should be filtered
        Assert.True(explicitMembers.Count < allMembers.Count,
            "Filtering should remove some implicit members");

        // No implicit members should remain
        Assert.DoesNotContain(explicitMembers, m => m.IsImplicitlyDeclared);
    }
}
