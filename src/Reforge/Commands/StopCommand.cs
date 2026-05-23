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

            var content = (await File.ReadAllTextAsync(portFile, cancellationToken)).Trim();
            if (!int.TryParse(content, out var port))
            {
                TryDelete(portFile);
                Console.WriteLine("Removed stale port file (invalid contents).");
                return;
            }

            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, port, cancellationToken);

                var stream = client.GetStream();
                using var writer = new StreamWriter(stream) { AutoFlush = true };
                using var reader = new StreamReader(stream);

                await writer.WriteLineAsync("__shutdown__");
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
