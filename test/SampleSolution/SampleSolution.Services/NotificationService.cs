using SampleSolution.Core.Interfaces;

namespace SampleSolution.Services;

/// <summary>
/// Tests: injected (takes IEmailSender), dependencies.
/// </summary>
public class NotificationService : INotificationService
{
    private readonly IEmailSender _emailSender;

    public NotificationService(IEmailSender emailSender)
    {
        _emailSender = emailSender;
    }

    public async Task NotifyAsync(string userId, string message, CancellationToken cancellationToken = default)
    {
        await _emailSender.SendAsync(
            $"{userId}@example.com",
            "Notification",
            message,
            cancellationToken);
    }
}
