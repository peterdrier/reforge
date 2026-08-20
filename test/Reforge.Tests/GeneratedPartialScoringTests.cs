namespace Reforge.Tests;

/// <summary>
/// The internal-complexity axis decides generated-ness per declaration. A partial type with a
/// generated half is one symbol spanning two files, and which of them Roslyn reports first is
/// declaration order rather than a fact about the code — so filtering on the primary file alone
/// made the score depend on it: the generated half's methods scored whenever the handwritten file
/// came first, and the handwritten half scored nothing whenever it did not.
/// </summary>
[Collection("SampleSolution")]
public class GeneratedPartialScoringTests
{
    private readonly SampleSolutionFixture _fixture;

    public GeneratedPartialScoringTests(SampleSolutionFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task GeneratedHalfOfPartialType_ScoresNothing()
    {
        var entries = await ScoreAsync();

        Assert.DoesNotContain(entries, e => e.File.EndsWith("GeneratedPartialFixture.Designer.cs", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(entries, e => e.Symbol.Contains("GeneratedWork", StringComparison.Ordinal));
    }

    [Fact]
    public async Task HandwrittenHalfOfPartialType_StillScores()
    {
        var entries = await ScoreAsync();

        // The other half of the same assertion: skipping the type outright — which is what the
        // primary-file filter did whenever the generated declaration came first — would leave the
        // handwritten complex method uncharged and the section quietly cheaper.
        var charge = Assert.Single(entries.Where(e =>
            e.Rule == "cognitiveComplexity" && e.Symbol.Contains("HandwrittenWork", StringComparison.Ordinal)));
        Assert.EndsWith("GeneratedPartialFixture.cs", charge.File, StringComparison.OrdinalIgnoreCase);
        Assert.True(charge.Points > 0);
    }

    [Fact]
    public async Task HandwrittenHalf_ScoresEvenWhenTheGeneratedDeclarationIsPrimary()
    {
        var entries = await ScoreAsync();

        // GeneratedPrimaryFixture's generated declaration sorts first, so it is the file
        // SolutionClassifier reports for the type — the ordering under which the old filter
        // discarded the handwritten half along with it.
        var charge = Assert.Single(entries.Where(e =>
            e.Rule == "cognitiveComplexity" && e.Symbol.Contains("HandwrittenSecondWork", StringComparison.Ordinal)));
        Assert.EndsWith("GeneratedPrimaryFixture.Bb.cs", charge.File, StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain(entries, e => e.Symbol.Contains("GeneratedPrimaryWork", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PartialMethod_DefinedInTheGeneratedHalf_IsScoredWhereItsBodyIs()
    {
        var entries = await ScoreAsync();

        // GetMembers() hands back the defining part, which lives in the generated file. Filtering its
        // declarations without resolving PartialImplementationPart first loses the handwritten body.
        var charge = Assert.Single(entries.Where(e =>
            e.Rule == "cognitiveComplexity" && e.Symbol.Contains("PartialWorkDefinedHere", StringComparison.Ordinal)));
        Assert.EndsWith("GeneratedPrimaryFixture.Bb.cs", charge.File, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<List<ScoreEntry>> ScoreAsync()
    {
        var cfg = SurfaceScoreConfig.Default();
        var engine = new SurfaceScoreEngine(cfg, LocationHelper.GetSolutionDirectory(_fixture.Solution));
        var report = await engine.ScoreAsync(_fixture.Solution, CancellationToken.None);
        return report.Groups.Values.SelectMany(g => g.Entries).ToList();
    }
}
