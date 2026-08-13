namespace Reforge.Tests;

[Collection("SampleSolution")]
public class SolutionClassifierTests
{
    private readonly SampleSolutionFixture _fixture;
    public SolutionClassifierTests(SampleSolutionFixture fixture) => _fixture = fixture;

    private async Task<List<ClassifiedType>> ClassifyAsync()
    {
        var cfg = SurfaceScoreConfig.Default();
        return (await SolutionClassifier.ClassifyAsync(
            _fixture.Solution, cfg, LocationHelper.GetSolutionDirectory(_fixture.Solution), CancellationToken.None)).ToList();
    }

    [Fact]
    public async Task ClassifyAsync_TagsKnownTypes()
    {
        var classified = await ClassifyAsync();

        Assert.Contains(classified, c => c.Type.Name == "UserService" && c.Tags.Contains("applicationService"));
        Assert.Contains(classified, c => c.Type.Name == "IUserService" && c.Tags.Contains("fullServiceInterface"));
        Assert.Equal(classified.Select(c => c.Type.ToDisplayString()).Distinct().Count(), classified.Count);
    }

    [Fact]
    public async Task ClassifyAsync_GroupsByContainingAssembly_WithNoSectionConfig()
    {
        var classified = await ClassifyAsync();

        // Group == the declaring assembly, common solution prefix stripped. Not the namespace,
        // and not the project that happened to enumerate the type first.
        Assert.Equal("Services", Group(classified, "UserService"));
        Assert.Equal("Core", Group(classified, "IUserRepository"));
        Assert.Equal("Camp", Group(classified, "CampSectionService"));
        Assert.Equal("Reporting", Group(classified, "CampReportBuilder"));

        Assert.All(classified, c => Assert.DoesNotContain('.', c.Group));
    }

    [Fact]
    public async Task ClassifyAsync_FoldsContractsAssemblyIntoParentSection()
    {
        var classified = await ClassifyAsync();

        // ICampServiceRead + CampInfo are declared in SampleSolution.Camp.Contracts; they must
        // land in the SAME section as ICampSectionService over in SampleSolution.Camp.
        Assert.Equal("Camp", Group(classified, "ICampServiceRead"));
        Assert.Equal("Camp", Group(classified, "CampInfo"));
        Assert.DoesNotContain(classified, c => c.Group.Contains("Contracts", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ClassifyAsync_ExcludesTestAssemblies()
    {
        var classified = await ClassifyAsync();
        Assert.DoesNotContain(classified, c => c.Group.Contains("Test", StringComparison.OrdinalIgnoreCase));
    }

    private static string Group(List<ClassifiedType> classified, string typeName)
        => classified.Single(c => c.Type.Name == typeName).Group;

    // ---------------- Assembly -> section derivation ----------------

    [Fact]
    public void AssemblySections_StripsSharedPrefix()
    {
        var map = AssemblySections.Resolve(new[] { "Humans.Application", "Humans.Web", "Humans.Store" });

        Assert.Equal("Application", map["Humans.Application"]);
        Assert.Equal("Web", map["Humans.Web"]);
        Assert.Equal("Store", map["Humans.Store"]);
    }

    [Fact]
    public void AssemblySections_FoldsContractsIntoParent()
    {
        var map = AssemblySections.Resolve(new[] { "Humans.Store", "Humans.Store.Contracts", "Humans.Web" });

        Assert.Equal("Store", map["Humans.Store"]);
        Assert.Equal("Store", map["Humans.Store.Contracts"]);
    }

    [Fact]
    public void AssemblySections_MonolithRemainder_KeepsItsOwnName()
    {
        // The not-yet-split assembly IS the shared prefix; stripping would leave nothing, so it
        // keeps its last segment rather than collapsing to an empty section name.
        var map = AssemblySections.Resolve(new[] { "Humans", "Humans.Store", "Humans.Store.Contracts" });

        Assert.Equal("Humans", map["Humans"]);
        Assert.Equal("Store", map["Humans.Store"]);
        Assert.Equal("Store", map["Humans.Store.Contracts"]);
    }

    [Fact]
    public void AssemblySections_NoSharedPrefix_KeepsFullNames()
    {
        var map = AssemblySections.Resolve(new[] { "Alpha.Core", "Beta.Core" });

        Assert.Equal("Alpha.Core", map["Alpha.Core"]);
        Assert.Equal("Beta.Core", map["Beta.Core"]);
    }

    [Fact]
    public void AssemblySections_IgnoresAssembliesThatDeclareNothing()
    {
        // Regression: Humans.slnx carries a `docs` project with no C#. Feeding it into the
        // prefix calculation left every section named "Humans.<X>". Only type-declaring
        // assemblies are passed in, so the prefix survives.
        var map = AssemblySections.Resolve(new[] { "Humans.Store", "Humans.Web" });
        Assert.Equal("Store", map["Humans.Store"]);

        var polluted = AssemblySections.Resolve(new[] { "Humans.Store", "Humans.Web", "docs" });
        Assert.Equal("Humans.Store", polluted["Humans.Store"]);
    }

    [Fact]
    public void AssemblySections_SingleAssembly_UsesLastSegment()
    {
        var map = AssemblySections.Resolve(new[] { "Contoso.Billing" });
        Assert.Equal("Billing", map["Contoso.Billing"]);
    }

    // ---------------- Section facts (policy overrides over derived repo-backing) ----------------

    private static IReadOnlySet<string> RepoSections(params string[] names)
        => new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void SectionFacts_RepoBacked_FromDeclaredRepository()
    {
        var facts = SectionFacts.For("Camp", SectionRule.None, RepoSections("Camp"));
        Assert.True(facts.RepoBacked);
        Assert.True(facts.RequiresReadSurface);
        Assert.True(facts.RequiresWriteSurface);
        Assert.True(facts.RequiresPrimaryInfoDto);
    }

    [Fact]
    public void SectionFacts_OrchestratorOnly_NotRequired()
    {
        var facts = SectionFacts.For("Reporting", SectionRule.None, RepoSections());
        Assert.False(facts.RepoBacked);
        Assert.False(facts.RequiresReadSurface);
    }

    [Fact]
    public void SectionFacts_RequiresOverride_Wins()
    {
        var facts = SectionFacts.For("Reporting", new SectionRule { RequiresReadSurface = true }, RepoSections());
        Assert.False(facts.RepoBacked);
        Assert.True(facts.RequiresReadSurface);
    }
}
