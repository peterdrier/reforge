namespace SampleSolution.Core.Interfaces;

public interface INotificationService
{
    Task NotifyAsync(string userId, string message, CancellationToken cancellationToken = default);
}
