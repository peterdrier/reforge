namespace SampleSolution.Core.Interfaces;

/// <summary>
/// Partial consumer whose read-only half is in this file and whose write call is in the other.
/// A single write call anywhere in the class cancels the rule, so this must not be charged.
/// </summary>
public partial class SplitWriteGreetingConsumer
{
    private readonly IGreetingService _greetings;

    public SplitWriteGreetingConsumer(IGreetingService greetings)
    {
        _greetings = greetings;
    }

    public async Task<string> ShowAsync(int userId) => await _greetings.GetGreetingAsync(userId);
}
