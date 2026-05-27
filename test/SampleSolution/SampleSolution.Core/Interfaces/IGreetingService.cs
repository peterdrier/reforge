namespace SampleSolution.Core.Interfaces;

/// <summary>
/// Full greeting service. Inherits the read interface and adds a write method.
/// Two test consumers exercise the writeCapableInterfaceUsedReadOnly rule:
/// <see cref="ReadOnlyGreetingConsumer"/> only calls read methods (should fire),
/// <see cref="FullGreetingConsumer"/> calls a write method (should not fire).
/// </summary>
public interface IGreetingService : IGreetingServiceRead
{
    Task RecordGreetingAsync(int userId, string message, CancellationToken cancellationToken = default);
}
