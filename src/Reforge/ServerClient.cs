using System.Net.Sockets;
using System.Reflection;

namespace Reforge;

/// <summary>
/// Checks for a running reforge server and relays commands to it.
///
/// <para><b>The handshake is out of band, in the port file, before a byte is sent.</b> A client and
/// a server built from different commits otherwise talk to each other and get subtly wrong answers:
/// an older server does not know the commands this client relays to it, so it replies with help
/// text; an older client does not know this server's framing, so it prints the frame as though it
/// were output. Neither can detect the other in-band, because the thing they disagree about
/// <i>is</i> the format.</para>
///
/// <para><b>Two things must match, and they answer different questions.</b>
/// <see cref="ProtocolVersion"/> asks "can we read each other's bytes?" — bump it when the framing
/// changes. <see cref="BuildIdentity"/> asks "will this server behave the way this client's user
/// expects?" — it changes on every build, because a command's contract can reverse while the
/// envelope stays identical.</para>
///
/// <para>Every mismatch degrades to the cold path — slower, always correct:</para>
/// <list type="bullet">
///   <item>A client predating the markers cannot parse a versioned port file
///     (<see cref="FormatPortFile"/> puts them on later lines, so its <c>int.TryParse</c>
///     over the whole file fails) and skips the relay.</item>
///   <item>A client reading any protocol other than its own — older or newer, marker or no marker —
///     says so on stderr and skips the relay.</item>
///   <item>A client finding a server from any other build says so and skips the relay, rather than
///     running that build's version of the command.</item>
/// </list>
/// </summary>
public static class ServerClient
{
    /// <summary>Wire-format version this build speaks. Bump whenever the framing below changes.</summary>
    public const int ProtocolVersion = 2;

    internal const string RequestHeader = "__reforge_req_v2__";
    internal const string ResponseHeader = "__reforge_res_v2__";

    /// <summary>Single-line request the <c>stop</c> command sends. Understood by every version.</summary>
    internal const string ShutdownRequest = "__shutdown__";

    /// <summary>
    /// A reachable server: its port, the protocol version it advertises (0 = predates the marker),
    /// and which build of reforge it is running ("" = predates that marker).
    /// </summary>
    public sealed record ServerEndpoint(int Port, int Protocol, string Build = "");

    /// <summary>
    /// Which build of reforge this process is: the package version, plus the module's identifier so
    /// two builds of the same version are still told apart.
    /// </summary>
    /// <remarks>
    /// The protocol version cannot stand in for this. It says the two sides agree on the envelope;
    /// it says nothing about what the commands inside it <i>do</i>. v0.27.0 is the case that proved
    /// the difference: it made <c>surface-score</c> refuse to score a broken build and exit 2, while
    /// v0.26.0 scored it and exited 0 — opposite contracts over an identical wire format. A client
    /// that relayed to the older server got the older behavior with nothing to warn it.
    /// <para>The module identifier matters as much as the version, because the version does not
    /// change between rebuilds from source — the exact case a developer hits while working on
    /// reforge itself, where the server is most likely to be stale.</para>
    /// </remarks>
    internal static string BuildIdentity { get; } = ComputeBuildIdentity();

    private static string ComputeBuildIdentity()
    {
        var assembly = typeof(ServerClient).Assembly;

        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "unknown";

        // Strip any "+<commit sha>" build metadata; the module id below identifies the build far
        // more precisely than a sha that is only present in some packaging configurations.
        var plus = version.IndexOf('+');
        if (plus >= 0) version = version[..plus];

        var moduleId = assembly.ManifestModule.ModuleVersionId.ToString("N");
        return $"{version}/{moduleId[..8]}";
    }

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

        // An EXACT match, not a minimum. Older is the case that motivated the gate — relaying to a
        // server whose command table predates the commands being sent is how the silent-success
        // bug survives an upgrade. But newer is no safer: the version exists to mark a framing
        // change, so a newer server's replies are by definition something this build cannot read, and
        // dispatching first would mean discovering that after the command may already have run.
        // Unknown means don't dispatch, in both directions.
        if (endpoint.Protocol != ProtocolVersion)
        {
            var direction = endpoint.Protocol < ProtocolVersion ? "an older" : "a newer";
            Console.Error.WriteLine(
                $"reforge: a hot server is running but speaks {direction} protocol " +
                $"(server v{endpoint.Protocol}, client v{ProtocolVersion}); using the cold path. " +
                "Run `reforge stop`, then restart `reforge serve` with a matching build, to use it.");
            return null;
        }

        // Same envelope is not the same behavior. A hot server is a cache of a *build*, and a
        // command's contract can reverse between builds that share a protocol version: v0.27.0 made
        // `surface-score` refuse a degraded build and exit 2 where v0.26.0 scored it and exited 0.
        // Relaying to a server left running from before an upgrade silently ran the old code, so the
        // identity that has to match is the build, not the wire format.
        if (!string.Equals(endpoint.Build, BuildIdentity, StringComparison.Ordinal))
        {
            var server = endpoint.Build.Length == 0 ? "an unidentified build" : $"build {endpoint.Build}";
            Console.Error.WriteLine(
                $"reforge: a hot server is running {server}, but this is {BuildIdentity}; using the cold " +
                "path so you get this build's behavior. Run `reforge stop && reforge serve` to make the " +
                "hot path available again.");
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
            await writer.WriteAsync(FormatRequest(Directory.GetCurrentDirectory(), args));

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

    // ---------------- Wire format ----------------

    /// <summary>
    /// Request framing: a header line carrying the length of the working directory, the argument
    /// count and each argument's length, then the working directory and the arguments concatenated.
    ///
    /// <para>The client's working directory travels with the request because the server runs in
    /// whatever directory it was started from: without it a relative <c>--config</c>,
    /// <c>--baseline</c> or <c>snapshot --append</c> path silently resolves against the server's
    /// directory rather than the caller's.</para>
    ///
    /// <para><b>Lengths, not a command line.</b> This used to join the arguments into a shell-like
    /// string that the server re-split. That round trip was lossy in three ways, and every one of
    /// them fails silently: an argument containing a literal <c>"</c> came out with the quote
    /// deleted (<c>snapshot --append 'daily"run.csv'</c> wrote to <c>dailyrun.csv</c> and reported
    /// success), an argument containing both a space and a quote came apart, and an empty argument
    /// vanished — shifting every positional argument after it. The process already <i>has</i> the
    /// argument array; re-deriving it from text is what introduced the ambiguity, so the array now
    /// travels as an array. This also makes the two directions symmetric — see
    /// <see cref="FormatResponse"/> for the same reasoning about output.</para>
    /// </summary>
    internal static string FormatRequest(string workingDirectory, IReadOnlyList<string> args)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(RequestHeader).Append(' ').Append(workingDirectory.Length).Append(' ').Append(args.Count);
        foreach (var arg in args)
            sb.Append(' ').Append(arg.Length);

        sb.Append('\n').Append(workingDirectory);
        foreach (var arg in args)
            sb.Append(arg);

        return sb.ToString();
    }

    /// <summary>
    /// Splits a framed request. Null when it isn't one — a pre-v2 client, or a truncated read.
    /// </summary>
    internal static (string WorkingDirectory, string[] Args)? ParseRequest(string request)
    {
        if (!request.StartsWith(RequestHeader, StringComparison.Ordinal)) return null;

        int newline = request.IndexOf('\n');
        if (newline < 0) return null;

        var header = request[..newline].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (header.Length < 3) return null;
        if (!int.TryParse(header[1], out var workingDirectoryLength) || workingDirectoryLength < 0) return null;
        if (!int.TryParse(header[2], out var argCount) || argCount < 0) return null;

        // Checked before allocating anything sized by argCount, so a malformed header cannot ask
        // this process for an arbitrarily large array.
        if (header.Length != 3 + argCount) return null;

        var lengths = new int[argCount];
        long total = workingDirectoryLength;
        for (int i = 0; i < argCount; i++)
        {
            if (!int.TryParse(header[3 + i], out lengths[i]) || lengths[i] < 0) return null;
            total += lengths[i];
        }

        // Exact, not "at least": every character of the body is claimed by a length, so a mismatch
        // in either direction means truncation or a framing bug, not extra data to ignore.
        var body = request[(newline + 1)..];
        if (total != body.Length) return null;

        var args = new string[argCount];
        int offset = workingDirectoryLength;
        for (int i = 0; i < argCount; i++)
        {
            args[i] = body.Substring(offset, lengths[i]);
            offset += lengths[i];
        }

        return (body[..workingDirectoryLength], args);
    }

    /// <summary>
    /// Response framing: a header line carrying the exit code and the length of the stderr
    /// section, then stderr, then stdout. Length-prefixed rather than delimited, so no command
    /// output can forge a section boundary and stdout survives character for character — it is
    /// JSON that a script parses, and a stray newline is a bug.
    /// </summary>
    internal static string FormatResponse(int exitCode, string stderr, string stdout)
        => $"{ResponseHeader} {exitCode} {stderr.Length}\n{stderr}{stdout}";

    /// <summary>Splits a framed response. Null when it isn't one (an older server, or a truncated read).</summary>
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
    /// line</b> deliberately: a client predating it parses the whole file with <c>int.TryParse</c>, so
    /// the extra line makes it find no server and take the cold path, instead of relaying into a
    /// framing it cannot read.
    /// </summary>
    public static string FormatPortFile(int port)
        => $"{port}\nprotocol={ProtocolVersion}\nbuild={BuildIdentity}\n";

    /// <summary>
    /// Reads a port file. Protocol is 0 and build is empty when those markers are absent — the shape
    /// an older server writes. Null when there is no port to be found at all.
    /// </summary>
    internal static ServerEndpoint? ParsePortFile(string content)
    {
        var lines = content.Split('\n');
        if (lines.Length == 0) return null;
        if (!int.TryParse(lines[0].Trim(), out var port)) return null;

        const string protocolMarker = "protocol=";
        const string buildMarker = "build=";

        int protocol = 0;
        string build = "";
        for (int i = 1; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.StartsWith(protocolMarker, StringComparison.Ordinal)
                && int.TryParse(line.AsSpan(protocolMarker.Length), out var version))
                protocol = version;
            else if (line.StartsWith(buildMarker, StringComparison.Ordinal))
                build = line[buildMarker.Length..];
        }

        return new ServerEndpoint(port, protocol, build);
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
