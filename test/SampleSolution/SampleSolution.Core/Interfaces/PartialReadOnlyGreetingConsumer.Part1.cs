namespace SampleSolution.Core.Interfaces;

/// <summary>
/// Read-only consumer of the full interface, split across two files. One consumer, one charge:
/// splitting a class must not multiply what it costs.
/// </summary>
public partial class PartialReadOnlyGreetingConsumer
{
    private readonly IGreetingService _greetings;

    public PartialReadOnlyGreetingConsumer(IGreetingService greetings)
    {
        _greetings = greetings;
    }

    public async Task<string> ShowAsync(int userId) => await _greetings.GetGreetingAsync(userId);
}
