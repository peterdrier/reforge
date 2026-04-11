using System.Text.Json;
using System.Text.Json.Serialization;

namespace Reforge;

public enum OutputFormat
{
    Compact,
    Json
}

/// <summary>
/// A single result entry with location and context, suitable for both JSON and compact output.
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
            WriteCompact(command, symbol, results, toEntry);
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

    private static void WriteCompact<T>(
        string command,
        string symbol,
        IReadOnlyList<T> results,
        Func<T, ResultEntry> toEntry)
    {
        var entries = results.Select(toEntry).ToList();

        Console.WriteLine($"{entries.Count} {command} of {symbol}");

        if (entries.Count == 0)
            return;

        Console.WriteLine();

        // Group by file, output grouped
        var grouped = entries.GroupBy(e => e.File);
        foreach (var group in grouped)
        {
            Console.WriteLine(group.Key);
            foreach (var e in group)
            {
                Console.WriteLine($"  {e.Line}: {e.Context}");
            }
            Console.WriteLine();
        }
    }
}
