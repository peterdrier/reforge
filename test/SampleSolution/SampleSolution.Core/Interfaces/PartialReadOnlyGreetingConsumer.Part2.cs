namespace SampleSolution.Core.Interfaces;

// The second half calls the dependency too, so a per-file count would charge this class twice.
public partial class PartialReadOnlyGreetingConsumer
{
    public async Task<string> ShowAgainAsync(int userId) => await _greetings.GetGreetingAsync(userId);
}
