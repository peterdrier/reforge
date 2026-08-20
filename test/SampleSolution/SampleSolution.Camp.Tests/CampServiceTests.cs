using SampleSolution.Camp;

namespace SampleSolution.Camp.Tests;

// Test mass attributed by project reference: this project references SampleSolution.Camp and
// nothing else, so it is Camp's whether or not its own name says so.
public class CampServiceTests
{
    public void GetByIdAsync_ReturnsTheCamp()
    {
        _ = typeof(ICampSectionService);
    }
}
