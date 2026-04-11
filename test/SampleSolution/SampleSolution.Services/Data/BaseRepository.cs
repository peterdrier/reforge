namespace SampleSolution.Services.Data;

/// <summary>
/// Base repository with a shared query method. Bottom of the 4-deep call chain:
/// Controller -> Service -> Repository -> BaseRepository.ExecuteQueryAsync
/// </summary>
public abstract class BaseRepository
{
    protected async Task<T?> ExecuteQueryAsync<T>(string query, CancellationToken cancellationToken = default)
        where T : class
    {
        // Simulate async DB query
        await Task.Delay(1, cancellationToken);
        return default;
    }

    protected async Task<List<T>> ExecuteQueryListAsync<T>(string query, CancellationToken cancellationToken = default)
        where T : class
    {
        await Task.Delay(1, cancellationToken);
        return [];
    }

    protected async Task ExecuteCommandAsync(string command, CancellationToken cancellationToken = default)
    {
        await Task.Delay(1, cancellationToken);
    }
}
