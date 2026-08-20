namespace SampleSolution.Lodge.IntegrationTests;

// References two sections (Lodge and Camp), so the references alone are ambiguous and the project
// name breaks the tie -> Lodge. An integration suite naturally reaches past the section it tests.
public class LodgeFlowTests
{
    public void BookAsync_WritesTheLodge()
    {
    }

    public void BookAsync_LeavesTheCampAlone()
    {
    }
}
