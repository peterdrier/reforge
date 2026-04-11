using SampleSolution.Core.Interfaces;
using SampleSolution.Services;

namespace SampleSolution.Web;

/// <summary>
/// Simulates DI service registration. Tests:
/// - nameof() references to service types
/// - Cross-project references (Web referencing Services types)
/// </summary>
public static class ServiceRegistration
{
    public static void RegisterServices(Dictionary<string, Type> container)
    {
        // nameof references -- tests that Reforge finds these as references to the types
        var userServiceName = nameof(UserService);
        var cachedUserServiceName = nameof(CachedUserService);
        var notificationServiceName = nameof(NotificationService);
        var orderServiceName = nameof(OrderService);

        // Type references
        container[userServiceName] = typeof(UserService);
        container[cachedUserServiceName] = typeof(CachedUserService);
        container[notificationServiceName] = typeof(NotificationService);
        container[orderServiceName] = typeof(OrderService);
    }

    /// <summary>
    /// Another method showing interface dispatch pattern.
    /// </summary>
    public static IUserService GetService(Dictionary<string, object> services)
    {
        // Interface dispatch: returning and using through interface type
        IUserService service = (IUserService)services[nameof(UserService)];
        return service;
    }
}
