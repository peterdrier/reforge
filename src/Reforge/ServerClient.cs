using System.Net.Sockets;

namespace Reforge;

/// <summary>
/// Checks for a running reforge server and relays commands to it.
/// </summary>
public static class ServerClient
{
    /// <summary>
    /// Attempts to relay the given args to a running reforge server.
    /// Returns true if relayed successfully, false if no server found.
    /// </summary>
    public static async Task<bool> TryRelayAsync(string[] args)
    {
        var port = FindServerPort(args);
        if (port is null)
            return false;

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

            // Read and print response
            var response = await reader.ReadToEndAsync();
            Console.Write(response);

            return true;
        }
        catch
        {
            // Server unreachable — fall back to cold start
            return false;
        }
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
