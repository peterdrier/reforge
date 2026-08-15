using System.Net.Sockets;

namespace Reforge;

/// <summary>
/// Checks for a running reforge server and relays commands to it.
///
/// <para><b>The wire format is versioned, and the version lives in the port file.</b> Without that,
/// a client and a server built from different commits talk to each other and get subtly wrong
/// answers: a pre-v1 server does not know the commands this client relays to it, so it replies
/// with help text; a pre-v1 client does not know this server's framing, so it prints the frame as
/// though it were output. Neither can detect the other in-band, because the thing they disagree
/// about <i>is</i> the format. So the handshake happens out of band, in <c>.reforge-port</c>,
/// before a byte is sent.</para>
///
/// <para>Both directions degrade to the cold path — slower, always correct:</para>
/// <list type="bullet">
///   <item>A pre-v1 client cannot parse a v1 port file (<see cref="FormatPortFile"/> puts the
///     protocol on a second line, so its <c>int.TryParse</c> over the whole file fails) and skips
///     the relay.</item>
///   <item>A v1 client reading a pre-v1 port file finds no protocol marker, says so on stderr, and
///     skips the relay.</item>
/// </list>
/// </summary>
public static class ServerClient
{
    /// <summary>Wire-format version this build speaks. Bump whenever the framing below changes.</summary>
    public const int ProtocolVersion = 1;

    internal const string RequestHeader = "__reforge_req_v1__";
    internal const string ResponseHeader = "__reforge_res_v1__";

    /// <summary>Single-line request the <c>stop</c> command sends. Understood by every version.</summary>
    internal const string ShutdownRequest = "__shutdown__";

    /// <summary>A reachable server: its port, and the protocol version it advertises (0 = pre-v1).</summary>
    public sealed record ServerEndpoint(int Port, int Protocol);

    /// <summary>
    /// Attempts to relay the given args to a running reforge server. Returns the command's exit
    /// code when the server ran it, or <c>null</c> when the caller should fall back to a cold
    /// start — no server, or one too old to talk to.
    /// </summary>
    /// <remarks>
    /// A completed round-trip is not treated as success on its own. The distinction that matters
    /// to a scripting agent is "the server ran this and it failed" (an exit code) versus "there is
    /// no usable server" (<c>null</c>); collapsing both into a bool is what let a server that
    /// could not dispatch a command look identical to one that could.
    /// </remarks>
    public static async Task<int?> TryRelayAsync(string[] args)
    {
        var endpoint = FindServerEndpoint(args);
        if (endpoint is null)
            return null;

        if (endpoint.Protocol < ProtocolVersion)
        {
            // Relaying anyway is how the silent-success bug survives an upgrade: the old server
            // does not know the commands this client now relays, answers with help text, and has
            // no way to say so. Cold-start instead, and name the fix.
            Console.Error.WriteLine(
                "reforge: a hot server is running but speaks an older protocol; using the cold path. " +
                "Run `reforge stop`, then restart `reforge serve`, to use it.");
            return null;
        }

        // Once any request byte is on the wire the server may have executed the command, so a
        // later failure must NOT fall back to the cold path — `snapshot --append` would write its
        // CSV row twice. Set before the write, not after: a partial write is still a possible
        // dispatch, and erring toward "report the failure" beats erring toward "do it again".
        bool dispatched = false;

        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(System.Net.IPAddress.Loopback, endpoint.Port);

            var stream = client.GetStream();
            using var writer = new StreamWriter(stream) { AutoFlush = true };
            using var reader = new StreamReader(stream);

            dispatched = true;
            await writer.WriteAsync(FormatRequest(Directory.GetCurrentDirectory(), BuildCommandLine(args)));

            // Shut down the write side so the server knows we're done
            client.Client.Shutdown(SocketShutdown.Send);

            var response = await reader.ReadToEndAsync();
            var parsed = ParseResponse(response);
            if (parsed is null)
            {
                Console.Error.WriteLine(
                    "reforge: the hot server sent a response this client cannot read. Not re-running on " +
                    "the cold path — the server may already have executed the command.");
                return 1;
            }

            var (exitCode, stderrText, stdoutText) = parsed.Value;
            if (stderrText.Length > 0)
                Console.Error.Write(stderrText);
            Console.Write(stdoutText);
            return exitCode;
        }
        catch (Exception ex)
        {
            if (!dispatched)
                return null; // nothing was sent — the cold path is safe, and is the better answer

            Console.Error.WriteLine(
                $"reforge: the relay failed after the command was sent ({ex.Message}). Not re-running on " +
                "the cold path — the server may already have executed it.");
            return 1;
        }
    }

    /// <summary>Joins args into the single request line, quoting any that contain a space.</summary>
    internal static string BuildCommandLine(string[] args)
        => string.Join(" ", args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a));

    // ---------------- Wire format ----------------

    /// <summary>
    /// Request framing. The client's working directory travels with the request because the server
    /// runs in whatever directory it was started from: without it a relative <c>--config</c>,
    /// <c>--baseline</c> or <c>snapshot --append</c> path silently resolves against the server's
    /// directory rather than the caller's. Each field is a whole line, so a path containing spaces
    /// needs no quoting rule.
    /// </summary>
    internal static string FormatRequest(string workingDirectory, string commandLine)
        => $"{RequestHeader}\n{workingDirectory}\n{commandLine}\n";

    /// <summary>
    /// Response framing: a header line carrying the exit code and the length of the stderr
    /// section, then stderr, then stdout. Length-prefixed rather than delimited, so no command
    /// output can forge a section boundary and stdout survives character for character — it is
    /// JSON that a script parses, and a stray newline is a bug.
    /// </summary>
    internal static string FormatResponse(int exitCode, string stderr, string stdout)
        => $"{ResponseHeader} {exitCode} {stderr.Length}\n{stderr}{stdout}";

    /// <summary>Splits a framed response. Null when it isn't one (a pre-v1 server, or a truncated read).</summary>
    internal static (int ExitCode, string Stderr, string Stdout)? ParseResponse(string response)
    {
        if (!response.StartsWith(ResponseHeader, StringComparison.Ordinal)) return null;

        int newline = response.IndexOf('\n');
        if (newline < 0) return null;

        var header = response[..newline].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (header.Length != 3) return null;
        if (!int.TryParse(header[1], out var exitCode)) return null;
        if (!int.TryParse(header[2], out var stderrLength) || stderrLength < 0) return null;

        var body = response[(newline + 1)..];
        if (stderrLength > body.Length) return null;

        return (exitCode, body[..stderrLength], body[stderrLength..]);
    }

    // ---------------- Port file ----------------

    /// <summary>
    /// Port-file contents for a server of this build. The protocol marker sits on a <b>second
    /// line</b> deliberately: a pre-v1 client parses the whole file with <c>int.TryParse</c>, so
    /// the extra line makes it find no server and take the cold path, instead of relaying into a
    /// framing it cannot read.
    /// </summary>
    public static string FormatPortFile(int port) => $"{port}\nprotocol={ProtocolVersion}\n";

    /// <summary>
    /// Reads a port file. Protocol is 0 when no marker is present — the shape a pre-v1 server
    /// writes. Null when there is no port to be found at all.
    /// </summary>
    internal static ServerEndpoint? ParsePortFile(string content)
    {
        var lines = content.Split('\n');
        if (lines.Length == 0) return null;
        if (!int.TryParse(lines[0].Trim(), out var port)) return null;

        int protocol = 0;
        const string marker = "protocol=";
        for (int i = 1; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.StartsWith(marker, StringComparison.Ordinal)
                && int.TryParse(line.AsSpan(marker.Length), out var version))
                protocol = version;
        }

        return new ServerEndpoint(port, protocol);
    }

    /// <summary>Resolves the running server's port and protocol from the .reforge-port file.</summary>
    public static ServerEndpoint? FindServerEndpoint(string[] args)
    {
        var portFile = FindPortFile(args);
        if (portFile is null) return null;

        try
        {
            return ParsePortFile(File.ReadAllText(portFile));
        }
        catch
        {
            return null; // unreadable port file — treat as no server
        }
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
