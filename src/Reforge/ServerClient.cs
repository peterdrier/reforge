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
        var port = FindServerPort();
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
    /// Searches upward from CWD for a .reforge-port file and reads the port number.
    /// </summary>
    private static int? FindServerPort()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir is not null)
        {
            var portFile = Path.Combine(dir.FullName, ".reforge-port");
            if (File.Exists(portFile))
            {
                var content = File.ReadAllText(portFile).Trim();
                if (int.TryParse(content, out var port))
                    return port;
            }
            dir = dir.Parent;
        }
        return null;
    }
}
