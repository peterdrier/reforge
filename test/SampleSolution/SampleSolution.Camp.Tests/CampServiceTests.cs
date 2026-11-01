using SampleSolution.Camp;
using Xunit;

namespace SampleSolution.Camp.Tests
{
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

        [SampleFact]
        public void SectionService_HasAName() => Assert(typeof(ICampSectionService).Name.Length > 0);

        // No test attribute, so not a test method.
        private static void Assert(bool condition)
        {
            if (!condition) throw new InvalidOperationException();
        }
    }

    // A project's own test attribute: counted because its base is xUnit's FactAttribute.
    [AttributeUsage(AttributeTargets.Method)]
    internal sealed class SampleFactAttribute : FactAttribute;
}

// Stands in for xUnit's: the sample references no test framework, and test attributes are matched
// by fully qualified name.
namespace Xunit
{
    [AttributeUsage(AttributeTargets.Method)]
    public class FactAttribute : Attribute;
}
