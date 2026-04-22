using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;

namespace Reforge.Tests;

[Collection("SampleSolution")]
public class ReferencesCommandTests
{
    private readonly SampleSolutionFixture _fixture;

    public ReferencesCommandTests(SampleSolutionFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task FindReferences_UserService_IncludesNameofReferences()
    {
        // Resolve UserService as a type (not its members)
        var symbols = await SymbolResolver.ResolveAsync(_fixture.Solution, "SampleSolution.Services.UserService");
        Assert.Single(symbols);

        var refs = await SymbolFinder.FindReferencesAsync(symbols[0], _fixture.Solution);
        var locations = refs.SelectMany(r => r.Locations).ToList();

        // Should include at least one nameof(UserService) reference
        Assert.True(locations.Count >= 1, $"Expected at least 1 reference, got {locations.Count}");

        var sourceTexts = locations.Select(l =>
        {
            var lineSpan = l.Location.GetLineSpan();
            var text = l.Location.SourceTree?.GetText();
            var lineNum = lineSpan.StartLinePosition.Line;
            return text?.Lines[lineNum].ToString().Trim() ?? "";
        }).ToList();

        Assert.Contains(sourceTexts, t => t.Contains("nameof"));
    }

    [Fact]
    public async Task FindReferences_IUserService_IncludesInterfaceDispatch()
    {
        var symbols = await SymbolResolver.ResolveAsync(_fixture.Solution, "SampleSolution.Core.Interfaces.IUserService");
        Assert.Single(symbols);

        var refs = await SymbolFinder.FindReferencesAsync(symbols[0], _fixture.Solution);
        var locations = refs.SelectMany(r => r.Locations).ToList();

        // IUserService is referenced in: UserController (field, ctor param, local variable),
        // CachedUserService (ctor param, field), OrderService (field, ctor param),
        // ServiceRegistration, and the interface implementations
        Assert.True(locations.Count >= 3, $"Expected at least 3 references, got {locations.Count}");

        // Verify that the interface dispatch usage in UserController is found
        // (IUserService service = _userService;)
        var sourceTexts = locations.Select(l =>
        {
            var lineSpan = l.Location.GetLineSpan();
            var text = l.Location.SourceTree?.GetText();
            var lineNum = lineSpan.StartLinePosition.Line;
            return text?.Lines[lineNum].ToString().Trim() ?? "";
        }).ToList();

        Assert.Contains(sourceTexts, t => t.Contains("IUserService service"));
    }

    [Fact]
    public async Task FindReferences_UserIsActive_IncludesLambdaParameterAccess()
    {
        // Regression for issue #5: property accessed through a lambda parameter
        // (e.g. Expression<Func<User, TProp>> in EF Core .Property(u => u.IsActive),
        // .Where(u => u.IsActive)) must show up in references.
        var symbols = await SymbolResolver.ResolveAsync(
            _fixture.Solution, "SampleSolution.Core.Models.User.IsActive");
        Assert.Single(symbols);

        var refs = await SymbolFinder.FindReferencesAsync(symbols[0], _fixture.Solution);
        var sourceTexts = refs.SelectMany(r => r.Locations)
            .Select(l =>
            {
                var lineSpan = l.Location.GetLineSpan();
                var text = l.Location.SourceTree?.GetText();
                var lineNum = lineSpan.StartLinePosition.Line;
                return text?.Lines[lineNum].ToString().Trim() ?? "";
            })
            .ToList();

        // Expression-tree lambda (EF Property configuration)
        Assert.Contains(sourceTexts, t => t.Contains("Property(u => u.IsActive)"));
        // Delegate lambda (LINQ Where)
        Assert.Contains(sourceTexts, t => t.Contains("Where(u => u.IsActive)"));
        // Direct member access on a variable in a different project
        Assert.Contains(sourceTexts, t => t.Contains("user.IsActive"));
    }

    [Fact]
    public async Task FindReferences_ServiceLifetimeAttribute_IncludesAttributeUsage()
    {
        var symbols = await SymbolResolver.ResolveAsync(
            _fixture.Solution, "SampleSolution.Core.Attributes.ServiceLifetimeAttribute");
        Assert.Single(symbols);

        var refs = await SymbolFinder.FindReferencesAsync(symbols[0], _fixture.Solution);
        var locations = refs.SelectMany(r => r.Locations).ToList();

        // ServiceLifetimeAttribute is used on UserService and CachedUserService
        Assert.True(locations.Count >= 2, $"Expected at least 2 attribute references, got {locations.Count}");

        var sourceTexts = locations.Select(l =>
        {
            var lineSpan = l.Location.GetLineSpan();
            var text = l.Location.SourceTree?.GetText();
            var lineNum = lineSpan.StartLinePosition.Line;
            return text?.Lines[lineNum].ToString().Trim() ?? "";
        }).ToList();

        Assert.Contains(sourceTexts, t => t.Contains("[ServiceLifetime"));
    }
}
