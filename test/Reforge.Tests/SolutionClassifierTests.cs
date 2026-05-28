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
}
