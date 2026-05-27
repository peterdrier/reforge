namespace SampleSolution.Core.Interfaces;

/// <summary>
/// Read-only sibling of <see cref="IGreetingService"/>. Used by surface-score's
/// writeCapableInterfaceUsedReadOnly rule to demonstrate the symbol-based pairing.
/// </summary>
public interface IGreetingServiceRead
{
    Task<string> GetGreetingAsync(int userId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> GetRecentGreetingsAsync(int userId, CancellationToken cancellationToken = default);
}
