namespace SampleSolution.Core.Interfaces;

/// <summary>
/// Simple custom logger interface to avoid Microsoft.Extensions.Logging dependency.
/// </summary>
public interface ILogger
{
    void LogInfo(string message);
    void LogWarning(string message);
    void LogError(string message, Exception? exception = null);
}
