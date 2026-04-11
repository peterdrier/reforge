using SampleSolution.Core.Dto;
using SampleSolution.Core.Interfaces;
using SampleSolution.Core.Models;

namespace SampleSolution.Web.Controllers;

/// <summary>
/// Tests: injected (takes IUserService + INotificationService), callers/call-chain (top of chain),
///        interface dispatch (calls through IUserService variable), nameof references.
/// </summary>
public class UserController
{
    private readonly IUserService _userService;
    private readonly INotificationService _notificationService;

    public UserController(IUserService userService, INotificationService notificationService)
    {
        _userService = userService;
        _notificationService = notificationService;
    }

    /// <summary>
    /// Top of the 4-deep call chain:
    /// GetUser -> IUserService.GetUserAsync -> IUserRepository.FindByIdAsync -> BaseRepository.ExecuteQueryAsync
    /// Also tests interface dispatch: calling GetUserAsync through an IUserService variable.
    /// </summary>
    public async Task<Core.Dto.User?> GetUser(int id, CancellationToken cancellationToken = default)
    {
        // Interface dispatch: calling method through interface variable (grep would miss this)
        IUserService service = _userService;
        var user = await service.GetUserAsync(id, cancellationToken);

        if (user == null)
        {
            return null;
        }

        // Cross-project reference: using both Models.User and Dto.User
        return MapToDto(user);
    }

    public async Task<IReadOnlyList<Core.Dto.User>> GetAllUsers(CancellationToken cancellationToken = default)
    {
        var users = await _userService.GetAllUsersAsync(cancellationToken);
        return users.Select(MapToDto).ToList().AsReadOnly();
    }

    public async Task NotifyUser(int userId, string message, CancellationToken cancellationToken = default)
    {
        var user = await _userService.GetUserAsync(userId, cancellationToken);
        if (user != null)
        {
            await _notificationService.NotifyAsync(
                userId.ToString(),
                message,
                cancellationToken);
        }
    }

    /// <summary>
    /// Uses nameof() to reference UserService -- tests nameof-based references.
    /// </summary>
    public string GetServiceName()
    {
        return nameof(Services.UserService);
    }

    private static Core.Dto.User MapToDto(Core.Models.User user)
    {
        return new Core.Dto.User
        {
            Id = user.Id,
            DisplayName = user.Name,
            Email = user.Email
        };
    }
}
