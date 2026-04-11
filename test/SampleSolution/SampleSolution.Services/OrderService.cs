using SampleSolution.Core.Interfaces;
using SampleSolution.Core.Models;
using SampleSolution.Services.Data;

namespace SampleSolution.Services;

/// <summary>
/// Tests: injected (takes IUserService + IOrderRepository), cross-service dependencies,
///        ownership-violations (accesses Users table which is owned by UserService).
/// </summary>
public class OrderService
{
    private readonly IUserService _userService;
    private readonly IOrderRepository _orderRepository;
    private readonly AppDbContext _dbContext;

    public OrderService(IUserService userService, IOrderRepository orderRepository, AppDbContext dbContext)
    {
        _userService = userService;
        _orderRepository = orderRepository;
        _dbContext = dbContext;
    }

    public async Task<object?> GetOrderForUserAsync(int userId, int orderId, CancellationToken cancellationToken = default)
    {
        // Ownership violation: OrderService accesses Users table directly
        var user = _dbContext.Users.FirstOrDefault(u => u.Id == userId);
        if (user == null)
        {
            return null;
        }

        return await _orderRepository.GetOrderAsync(orderId, cancellationToken);
    }

    /// <summary>
    /// Method with optional and default parameters for testing `parameters` command.
    /// </summary>
    public async Task<IReadOnlyList<object>> SearchOrdersAsync(
        int userId,
        string? status = null,
        DateTime? fromDate = null,
        DateTime? toDate = null,
        int page = 1,
        int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        var user = await _userService.GetUserAsync(userId, cancellationToken);
        if (user == null)
        {
            return [];
        }

        return await _orderRepository.GetOrdersByUserAsync(userId, cancellationToken);
    }
}
