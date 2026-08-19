namespace Reforge.Tests;

/// <summary>
/// The shared production-document walk. The cancellation test is the load-bearing one: ending the
/// sequence early instead of throwing would let a cancelled audit print partial findings and exit 0,
/// which every automated consumer reads as "no findings".
/// </summary>
[Collection("SampleSolution")]
public class SolutionWalkerTests
{
    private readonly SampleSolutionFixture _fixture;

    public SolutionWalkerTests(SampleSolutionFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ProductionDocuments_SkipsTestProjectsAndYieldsRootAndModel()
    {
        var seen = new List<SolutionDocument>();
        await foreach (var doc in SolutionWalker.ProductionDocumentsAsync(_fixture.Solution, CancellationToken.None))
            seen.Add(doc);

        Assert.NotEmpty(seen);
        Assert.All(seen, d =>
        {
            Assert.False(SolutionWalker.IsTestProject(d.Project));
            Assert.NotNull(d.Root);
            Assert.NotNull(d.Model);
            // The model must belong to the document it was yielded with, or every call site that
            // resolves a symbol from Root against Model is silently wrong.
            Assert.Same(d.Root.SyntaxTree, d.Model.SyntaxTree);
        });
    }

    [Fact]
    public async Task ProductionDocuments_CancellationThrowsRatherThanEndingTheSequence()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in SolutionWalker.ProductionDocumentsAsync(_fixture.Solution, cts.Token))
            {
                // Unreachable: the first project must throw.
            }
        });
    }

    [Fact]
    public async Task ProductionDocuments_CancellationMidWalkAlsoThrows()
    {
        using var cts = new CancellationTokenSource();
        int seen = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in SolutionWalker.ProductionDocumentsAsync(_fixture.Solution, cts.Token))
            {
                if (++seen == 1) cts.Cancel();
            }
        });

        Assert.Equal(1, seen);
    }
}
