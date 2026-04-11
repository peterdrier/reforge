using System.Text.Json;
using System.Text.Json.Serialization;

namespace Reforge;

public enum OutputFormat
{
    Json,
    Table
}

/// <summary>
/// A single result entry with location and context, suitable for both JSON and table output.
/// </summary>
public record ResultEntry(
    string File,
    int Line,
    int Column,
    string Context,
    string ContainingSymbol);

public static class OutputFormatter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Writes a result set to stdout in the requested format.
    /// </summary>
    /// <typeparam name="T">The source result type.</typeparam>
    /// <param name="command">The command name (e.g., "references").</param>
    /// <param name="symbol">The symbol that was queried.</param>
    /// <param name="results">The result items.</param>
    /// <param name="format">JSON or Table output format.</param>
    /// <param name="toEntry">Converts a source item to a ResultEntry for output.</param>
    public static void WriteResults<T>(
        string command,
        string symbol,
        IReadOnlyList<T> results,
        OutputFormat format,
        Func<T, ResultEntry> toEntry)
    {
        if (format == OutputFormat.Json)
            WriteJson(command, symbol, results, toEntry);
        else
            WriteTable(results, toEntry);
    }

    /// <summary>
    /// Writes a single message (error or info) to stdout in the requested format.
    /// </summary>
    public static void WriteMessage(string command, string message, OutputFormat format)
    {
        if (format == OutputFormat.Json)
        {
            var output = new { command, message };
            Console.WriteLine(JsonSerializer.Serialize(output, JsonOptions));
        }
        else
        {
            Console.WriteLine(message);
        }
    }

    private static void WriteJson<T>(
        string command,
        string symbol,
        IReadOnlyList<T> results,
        Func<T, ResultEntry> toEntry)
    {
        var entries = results.Select(toEntry).ToList();
        var output = new
        {
            command,
            symbol,
            results = entries.Select(e => new
            {
                file = e.File,
                line = e.Line,
                column = e.Column,
                context = e.Context,
                containingSymbol = e.ContainingSymbol
            }).ToArray(),
            total = entries.Count
        };

        Console.WriteLine(JsonSerializer.Serialize(output, JsonOptions));
    }

    private static void WriteTable<T>(
        IReadOnlyList<T> results,
        Func<T, ResultEntry> toEntry)
    {
        foreach (var item in results)
        {
            var e = toEntry(item);
            // Format: file:line  containingSymbol  context
            Console.WriteLine($"{e.File}:{e.Line}  {e.ContainingSymbol}  {e.Context}");
        }
    }
}
