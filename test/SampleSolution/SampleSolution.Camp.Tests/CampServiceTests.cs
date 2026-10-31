using SampleSolution.Camp;

namespace SampleSolution.Camp.Tests;

// Test mass attributed by project reference: this project references SampleSolution.Camp and
// nothing else, so it is Camp's whether or not its own name says so.
public class CampServiceTests
{
    [Fact]
    public void GetByIdAsync_ReturnsTheCamp()
    {
        _ = typeof(ICampSectionService);
    }

    [FactAttribute]
    public void SectionService_IsAnInterface() => Assert(typeof(ICampSectionService).IsInterface);

    // No test attribute, so not a test method.
    private static void Assert(bool condition)
    {
        if (!condition) throw new InvalidOperationException();
    }
}

// Stands in for xUnit's: the sample references no test framework, and test methods are counted by attribute name.
[AttributeUsage(AttributeTargets.Method)]
internal sealed class FactAttribute : Attribute;
