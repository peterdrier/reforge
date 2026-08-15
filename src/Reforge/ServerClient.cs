using System.Net.Sockets;

namespace Reforge;

/// <summary>
/// Checks for a running reforge server and relays commands to it.
/// </summary>
public static class ServerClient
{
    /// <summary>
    /// Prefix of the status line the server sends ahead of a command's captured output, carrying
    /// that command's exit code. Without it the client could only report "the socket round-trip
    /// completed", which is what let a command the server could not dispatch look like success.
    /// </summary>
    public const string ExitCodeSentinel = "__reforge_exit__:";

    /// <summary>
    /// Attempts to relay the given args to a running reforge server. Returns the command's exit
    /// code when the server ran it, or <c>null</c> when no server is reachable and the caller
    /// should fall back to a cold start.
    /// </summary>
    /// <remarks>
    /// A completed round-trip is no longer treated as success on its own. The distinction that
    /// matters to a scripting agent is "the server ran this and it failed" (an exit code) versus
    /// "there is no server" (<c>null</c>) — collapsing both into <c>true</c>/<c>false</c> is what
    /// made a server that could not dispatch a command indistinguishable from one that could.
    /// </remarks>
    public static async Task<int?> TryRelayAsync(string[] args)
    {
        var port = FindServerPort(args);
        if (port is null)
            return null;

        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(System.Net.IPAddress.Loopback, port.Value);

            var stream = client.GetStream();
            using var writer = new StreamWriter(stream) { AutoFlush = true };
            using var reader = new StreamReader(stream);

            // Send command as single line
            var commandLine = string.Join(" ", args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a));
            await writer.WriteLineAsync(commandLine);

            // Shut down the write side so server knows we're done
            client.Client.Shutdown(SocketShutdown.Send);

            var response = await reader.ReadToEndAsync();
            var (exitCode, payload) = ParseResponse(response);
            Console.Write(payload);
            return exitCode;
        }
        catch
        {
            // Server unreachable — fall back to cold start
            return null;
        }
    }

    /// <summary>
    /// Splits a server response into its exit code and the command's output.
    /// </summary>
    /// <remarks>
    /// A response with no sentinel comes from a server binary older than the sentinel — a real
    /// case, since a server can outlive the client build that started it. It is passed through
    /// unchanged and reported as success: exactly the pre-sentinel behavior, rather than a
    /// spurious failure against a long-running older server.
    /// </remarks>
    internal static (int ExitCode, string Payload) ParseResponse(string response)
    {
        if (!response.StartsWith(ExitCodeSentinel, StringComparison.Ordinal))
            return (0, response);

        // Slice rather than ReadLine so the payload keeps its exact bytes, including whether its
        // final line is newline-terminated.
        int newline = response.IndexOf('\n');
        var codeText = (newline < 0 ? response[ExitCodeSentinel.Length..] : response[ExitCodeSentinel.Length..newline]).Trim();
        var payload = newline < 0 ? "" : response[(newline + 1)..];

        return (int.TryParse(codeText, out var exitCode) ? exitCode : 0, payload);
    }

    /// <summary>
    /// Resolves the port of a running server by reading the .reforge-port file.
    /// </summary>
    public static int? FindServerPort(string[] args)
    {
        var portFile = FindPortFile(args);
        if (portFile is null)
            return null;

        var content = File.ReadAllText(portFile).Trim();
        return int.TryParse(content, out var port) ? port : null;
    }

    /// <summary>
    /// Locates the .reforge-port file: the --solution directory first (most reliable),
    /// then searching upward from CWD. Returns the path if it exists, else null.
    /// </summary>
    public static string? FindPortFile(string[] args)
    {
        var solutionDir = GetSolutionDirFromArgs(args);
        if (solutionDir is not null)
        {
            var portFile = Path.Combine(solutionDir, ".reforge-port");
            if (File.Exists(portFile))
                return portFile;
        }

        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir is not null)
        {
            var portFile = Path.Combine(dir.FullName, ".reforge-port");
            if (File.Exists(portFile))
                return portFile;
            dir = dir.Parent;
        }
        return null;
    }

    private static string? GetSolutionDirFromArgs(string[] args)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--solution")
            {
                var solutionPath = args[i + 1];
                if (File.Exists(solutionPath))
                    return Path.GetDirectoryName(Path.GetFullPath(solutionPath));
            }
        }
        return null;
    }
}
