namespace Reforge.Tests;

[Collection("SampleSolution")]
public class SolutionClassifierTests
{
    private readonly SampleSolutionFixture _fixture;
    public SolutionClassifierTests(SampleSolutionFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task ClassifyAsync_TagsKnownTypes()
    {
        var cfg = SurfaceScoreConfig.Default();
        var classified = await SolutionClassifier.ClassifyAsync(
            _fixture.Solution, cfg, LocationHelper.GetSolutionDirectory(_fixture.Solution), CancellationToken.None);

        Assert.Contains(classified, c => c.Type.Name == "UserService" && c.Tags.Contains("applicationService"));
        Assert.Contains(classified, c => c.Type.Name == "IUserService" && c.Tags.Contains("fullServiceInterface"));
        Assert.Equal(classified.Select(c => c.Type.ToDisplayString()).Distinct().Count(), classified.Count);
    }

    [Fact]
    public void SectionFacts_RepoBacked_FromConfiguredRepository()
    {
        var rule = new SectionRule { Name = "Camp", RepositoryInterfaces = { "ICampRepository" } };
        var facts = SectionFacts.For(rule, classifiedRepoSectionNames: new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        Assert.True(facts.RepoBacked);
        Assert.True(facts.RequiresReadSurface);
        Assert.True(facts.RequiresWriteSurface);
        Assert.True(facts.RequiresPrimaryInfoDto);
    }

    [Fact]
    public void SectionFacts_OrchestratorOnly_NotRequired()
    {
        var rule = new SectionRule { Name = "Orchestrator" };
        var facts = SectionFacts.For(rule, classifiedRepoSectionNames: new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        Assert.False(facts.RepoBacked);
        Assert.False(facts.RequiresReadSurface);
    }

    [Fact]
    public void SectionFacts_RequiresOverride_Wins()
    {
        var rule = new SectionRule { Name = "Orchestrator", RequiresReadSurface = true };
        var facts = SectionFacts.For(rule, classifiedRepoSectionNames: new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        Assert.False(facts.RepoBacked);
        Assert.True(facts.RequiresReadSurface);
    }

    [Fact]
    public void SectionFacts_RepoBacked_FromClassifiedRepositoryInSection()
    {
        var rule = new SectionRule { Name = "Camp" };
        var facts = SectionFacts.For(rule,
            classifiedRepoSectionNames: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Camp" });
        Assert.True(facts.RepoBacked);
        Assert.True(facts.RequiresWriteSurface);
    }
}
