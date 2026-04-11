namespace SampleSolution.Core.Interfaces;

public interface IOrderRepository
{
    Task<object?> GetOrderAsync(int orderId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<object>> GetOrdersByUserAsync(int userId, CancellationToken cancellationToken = default);
}
