using System.CommandLine;
using System.Net;
using System.Net.Sockets;

namespace Reforge.Commands;

public static class StopCommand
{
    public static Command Create(Option<string?> solutionOption)
    {
        var command = new Command("stop", "Stop a running reforge hot server and clean up its port file");

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var solutionPath = parseResult.GetValue(solutionOption);
            var args = solutionPath is null ? Array.Empty<string>() : new[] { "--solution", solutionPath };

            var portFile = ServerClient.FindPortFile(args);
            if (portFile is null)
            {
                Console.WriteLine("No running server found.");
                return;
            }

            // Parsed through ServerClient so `stop` understands both the v1 port file and the bare
            // "just a port number" one a pre-v1 server leaves behind. Stopping a server must work
            // across versions even when relaying to it would not — otherwise the fix for a
            // version mismatch ("run reforge stop") would itself need a matching build.
            var endpoint = ServerClient.ParsePortFile(await File.ReadAllTextAsync(portFile, cancellationToken));
            if (endpoint is null)
            {
                TryDelete(portFile);
                Console.WriteLine("Removed stale port file (invalid contents).");
                return;
            }

            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, endpoint.Port, cancellationToken);

                var stream = client.GetStream();
                using var writer = new StreamWriter(stream) { AutoFlush = true };
                using var reader = new StreamReader(stream);

                // A bare line, not a framed request: every server version answers it.
                await writer.WriteLineAsync(ServerClient.ShutdownRequest);
                client.Client.Shutdown(SocketShutdown.Send);
                await reader.ReadToEndAsync(cancellationToken);

                Console.WriteLine("Server stopped.");
            }
            catch
            {
                // Server unreachable — it was hard-killed and left a stale port file.
                TryDelete(portFile);
                Console.WriteLine("Removed stale port file (server not responding).");
            }
        });

        return command;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }
}
