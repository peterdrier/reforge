namespace SampleSolution.Core.Interfaces;

/// <summary>
/// Holds the full IGreetingService, only calls read methods, and lives in the SAME assembly as
/// the interface. Same-section use keeps the generic writeCapableInterfaceUsedReadOnly rule —
/// the consumer is in the same assembly as the interface it holds.
/// </summary>
public class SameSectionGreetingConsumer
{
    private readonly IGreetingService _greetings;

    public SameSectionGreetingConsumer(IGreetingService greetings)
    {
        _greetings = greetings;
    }

    public async Task<string> ShowAsync(int userId)
    {
        var greeting = await _greetings.GetGreetingAsync(userId);
        return greeting;
    }
}
