using Microsoft.CodeAnalysis;

namespace Reforge.Tests;

/// <summary>
/// Where the data-carrier walk stops, and what it counts once it is past the solution boundary.
/// The two halves of the question separate there: behaviour declared outside the solution is still
/// callable on the deriving type, while data declared outside it is not that section's published
/// shape. Conflating them was wrong in both directions — counting outside data admitted an EF
/// migration as a read DTO, and stopping the walk outright lost the
/// <c>class SearchHit : List&lt;int&gt;</c> catch.
/// </summary>
[Collection("SampleSolution")]
public class DataCarrierBoundaryTests
{
    private readonly SampleSolutionFixture _fixture;

    public DataCarrierBoundaryTests(SampleSolutionFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task DataFromOutsideTheSolution_IsNotTheTypesOwnShape()
    {
        var (type, everything) = await FindAsync("BorrowedShapeInfo");

        // Whole shape inherited: inside one solution the base's properties are published by this
        // section, so the type carries data.
        Assert.True(CanonicalReadDtoSet.IsDataCarrier(type, everything));

        // Same type, base now outside the analysed set. Nothing it declares itself is data, so
        // there is no shape left to publish — the EF-migration case.
        Assert.False(CanonicalReadDtoSet.IsDataCarrier(type, Without(everything, "SampleSolution.Camp")));
    }

    [Fact]
    public async Task BehaviourFromOutsideTheSolution_StillDisqualifies()
    {
        var (type, everything) = await FindAsync("BorrowedBehaviourInfo");

        // A public method on the base is callable on this type from anywhere. Declaring the base
        // elsewhere does not withdraw it, so the verdict cannot depend on the boundary.
        Assert.False(CanonicalReadDtoSet.IsDataCarrier(type, everything));
        Assert.False(CanonicalReadDtoSet.IsDataCarrier(type, Without(everything, "SampleSolution.Camp")));

        // Same answer for the scorer's variant, which differs only in counting indexers as data.
        Assert.False(CanonicalReadDtoSet.IsDataCarrier(
            type, Without(everything, "SampleSolution.Camp"), countIndexersAsData: true));
    }

    [Fact]
    public async Task ExplicitlyImplementedInterfaceEvent_IsBehaviour()
    {
        var (type, everything) = await FindAsync("ExplicitEventReportInfo");

        // Private on the symbol, subscribable by anyone holding the interface. The interface's own
        // declaration is abstract, so the AllInterfaces scan does not see it either.
        Assert.False(CanonicalReadDtoSet.IsDataCarrier(type, everything));
    }

    private static HashSet<string> Without(HashSet<string> assemblies, string excluded)
    {
        var narrowed = new HashSet<string>(assemblies, StringComparer.Ordinal);
        Assert.True(narrowed.Remove(excluded), $"{excluded} is not in the analysed set");
        return narrowed;
    }

    private async Task<(INamedTypeSymbol Type, HashSet<string> AnalyzedAssemblies)> FindAsync(string name)
    {
        var cfg = SurfaceScoreConfig.Default();
        var classified = await SolutionClassifier.ClassifyAsync(
            _fixture.Solution, cfg, LocationHelper.GetSolutionDirectory(_fixture.Solution), CancellationToken.None);
        return (classified.Single(c => c.Type.Name == name).Type,
                CanonicalReadDtoSet.AnalyzedAssemblies(classified));
    }
}
