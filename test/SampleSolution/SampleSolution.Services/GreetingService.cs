using SampleSolution.Core.Interfaces;

namespace SampleSolution.Services;

public class GreetingService : IGreetingService
{
    public Task<string> GetGreetingAsync(int userId, CancellationToken cancellationToken = default)
        => Task.FromResult($"Hello {userId}");

    public Task<IReadOnlyList<string>> GetRecentGreetingsAsync(int userId, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<string>>(new List<string>());

    public Task RecordGreetingAsync(int userId, string message, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}

/// <summary>
/// Holds the full IGreetingService but only ever calls read methods on it.
/// Expected: writeCapableInterfaceUsedReadOnly fires on this class.
/// </summary>
public class ReadOnlyGreetingConsumer
{
    private readonly IGreetingService _greetings;

    public ReadOnlyGreetingConsumer(IGreetingService greetings)
    {
        _greetings = greetings;
    }

    public async Task<string> ShowAsync(int userId)
    {
        var greeting = await _greetings.GetGreetingAsync(userId);
        var recent = await _greetings.GetRecentGreetingsAsync(userId);
        return $"{greeting} ({recent.Count} recent)";
    }
}

/// <summary>
/// Holds the full IGreetingService and calls a write method.
/// Expected: writeCapableInterfaceUsedReadOnly does NOT fire on this class.
/// </summary>
public class FullGreetingConsumer
{
    private readonly IGreetingService _greetings;

    public FullGreetingConsumer(IGreetingService greetings)
    {
        _greetings = greetings;
    }

    public async Task RecordAsync(int userId, string message)
    {
        await _greetings.RecordGreetingAsync(userId, message);
    }
}
