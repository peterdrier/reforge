namespace Reforge;

public static class Telemetry
{
    private static readonly string LogDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".reforge");
    private static readonly string LogFile = Path.Combine(LogDir, "usage.log");

    public static void Log(string command, string args, int resultCount, long elapsedMs)
    {
        try
        {
            Directory.CreateDirectory(LogDir);
            var entry = $"[{DateTime.UtcNow:O}] {command} {args} | {resultCount} results | {elapsedMs}ms";
            File.AppendAllText(LogFile, entry + Environment.NewLine);
        }
        catch
        {
            // Never fail a command because telemetry failed
        }
    }
}
