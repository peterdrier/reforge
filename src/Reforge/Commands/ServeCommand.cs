using System.CommandLine;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.CodeAnalysis.MSBuild;

namespace Reforge.Commands;

public static class ServeCommand
{
    public static Command Create(Option<string?> solutionOption)
    {
        var portOption = new Option<int>("--port")
        {
            Description = "TCP port to listen on (default: auto-assign)",
            DefaultValueFactory = _ => 0
        };

        var idleTimeoutOption = new Option<int>("--idle-timeout")
        {
            Description = "Minutes of inactivity before auto-shutdown (default: 5, 0 to disable)",
            DefaultValueFactory = _ => 5
        };

        var command = new Command("serve", "Start hot workspace server for fast repeated queries")
        {
            portOption,
            idleTimeoutOption
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var solutionPath = parseResult.GetValue(solutionOption);

            // Open workspace once
            Console.Error.WriteLine("Loading workspace...");
            var (solution, workspace) = await OpenWorkspaceCold(solutionPath);

            // Set hot workspace so commands skip re-opening
            WorkspaceHelper.HotSolution = solution;

            // Find solution directory for port file
            var solutionDir = Path.GetDirectoryName(solution.FilePath) ?? Directory.GetCurrentDirectory();
            var portFile = Path.Combine(solutionDir, ".reforge-port");

            // Start TCP listener
            var listener = new TcpListener(IPAddress.Loopback, parseResult.GetValue(portOption));
            listener.Start();
            var actualPort = ((IPEndPoint)listener.LocalEndpoint).Port;

            // Write port file. The contents carry the protocol version as well as the port — see
            // ServerClient: it is the only place a client and server built from different commits
            // can discover the mismatch before committing to a wire format neither can parse.
            await File.WriteAllTextAsync(portFile, ServerClient.FormatPortFile(actualPort), cancellationToken);

            Console.Error.WriteLine($"Reforge server listening on port {actualPort}");
            Console.Error.WriteLine($"Port file: {portFile}");
            Console.Error.WriteLine("Press Ctrl+C to stop.");

            // Watch for file changes and reload workspace when source files change
            var solutionFilePath = solution.FilePath!;
            var watcher = new FileSystemWatcher(solutionDir)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
                EnableRaisingEvents = true
            };

            watcher.Filters.Add("*.cs");
            watcher.Filters.Add("*.csproj");
            watcher.Filters.Add("*.slnx");
            watcher.Filters.Add("*.sln");

            var reloadLock = new SemaphoreSlim(1, 1);
            Timer? debounceTimer = null;

            void OnFileChanged(object sender, FileSystemEventArgs e)
            {
                // Skip bin/obj directories
                if (e.FullPath.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar) ||
                    e.FullPath.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) ||
                    e.FullPath.Contains("/bin/") || e.FullPath.Contains("/obj/"))
                    return;

                debounceTimer?.Dispose();
                debounceTimer = new Timer(async _ =>
                {
                    if (await reloadLock.WaitAsync(0)) // Non-blocking try
                    {
                        try
                        {
                            Console.Error.WriteLine("File changes detected, reloading workspace...");
                            var sw = Stopwatch.StartNew();

                            var newWorkspace = MSBuildWorkspace.Create();
                            newWorkspace.RegisterWorkspaceFailedHandler(evt =>
                            {
                                // Suppress diagnostics during reload
                            });
                            var newSolution = await newWorkspace.OpenSolutionAsync(solutionFilePath);

                            // Atomic swap — old solution remains usable for in-flight queries
                            WorkspaceHelper.HotSolution = newSolution;

                            // Let the old workspace get GC'd rather than disposing it
                            // while in-flight queries might still reference the old solution

                            sw.Stop();
                            Console.Error.WriteLine($"Workspace reloaded in {sw.ElapsedMilliseconds}ms");
                        }
                        catch (Exception ex)
                        {
                            Console.Error.WriteLine($"Reload failed: {ex.Message}");
                        }
                        finally
                        {
                            reloadLock.Release();
                        }
                    }
                }, null, 500, Timeout.Infinite);
            }

            watcher.Changed += OnFileChanged;
            watcher.Created += OnFileChanged;
            watcher.Deleted += OnFileChanged;
            watcher.Renamed += (s, e) => OnFileChanged(s, e);

            // Shutdown is triggered by Ctrl+C (framework token), the `stop` command
            // (shutdown sentinel), or the idle timeout — all converge on this token so
            // the finally block's cleanup runs exactly once.
            using var shutdownCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            var idleMinutes = parseResult.GetValue(idleTimeoutOption);
            var idleMs = idleMinutes > 0 ? idleMinutes * 60_000 : Timeout.Infinite;
            Timer? idleTimer = null;
            if (idleMs != Timeout.Infinite)
            {
                Console.Error.WriteLine($"Idle timeout: {idleMinutes} min.");
                idleTimer = new Timer(_ =>
                {
                    Console.Error.WriteLine($"Idle timeout ({idleMinutes} min) reached, shutting down.");
                    shutdownCts.Cancel();
                }, null, idleMs, Timeout.Infinite);
            }

            try
            {
                while (!shutdownCts.IsCancellationRequested)
                {
                    TcpClient client;
                    try
                    {
                        client = await listener.AcceptTcpClientAsync(shutdownCts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    // Pause the idle timer while handling so an in-flight query is never
                    // interrupted; restart it once the query completes.
                    idleTimer?.Change(Timeout.Infinite, Timeout.Infinite);

                    // Process clients sequentially — Roslyn isn't thread-safe for mutations
                    await HandleClientAsync(client, shutdownCts);

                    idleTimer?.Change(idleMs, Timeout.Infinite);
                }
            }
            finally
            {
                idleTimer?.Dispose();
                debounceTimer?.Dispose();
                watcher.Dispose();
                listener.Stop();
                WorkspaceHelper.HotSolution = null;
                workspace.Dispose();

                // Clean up port file
                try { File.Delete(portFile); } catch { }
            }
        });

        return command;
    }

    private static async Task<(Microsoft.CodeAnalysis.Solution, MSBuildWorkspace)> OpenWorkspaceCold(string? solutionPath)
    {
        var resolved = solutionPath ?? WorkspaceHelper.FindSolutionFile();

        var workspace = MSBuildWorkspace.Create();
        workspace.RegisterWorkspaceFailedHandler(e =>
        {
            Console.Error.WriteLine($"workspace: {e.Diagnostic.Message}");
        });

        var solution = await workspace.OpenSolutionAsync(resolved);
        return (solution, workspace);
    }

    private static async Task HandleClientAsync(TcpClient client, CancellationTokenSource shutdownCts)
    {
        using (client)
        {
            StreamWriter? writer = null;
            try
            {
                var stream = client.GetStream();
                using var reader = new StreamReader(stream);
                writer = new StreamWriter(stream) { AutoFlush = true };

                var result = await RunRequestAsync(reader, shutdownCts);
                await WriteResultAsync(writer, result);
            }
            catch (Exception ex)
            {
                // Answer the client even when handling failed. Staying silent here left the client
                // reading an empty response, which it could only interpret as "ran, printed
                // nothing" — a crash indistinguishable from a clean run with no findings.
                Console.Error.WriteLine($"Client error: {ex.Message}");
                if (writer is not null)
                {
                    try
                    {
                        await WriteResultAsync(writer,
                            new RequestResult(1, $"error: {ex.Message}{Environment.NewLine}", ""));
                    }
                    catch
                    {
                        // The socket is already gone — the stderr line above is the only record
                        // left, and failing here would take down the accept loop with it.
                    }
                }
            }
            finally
            {
                writer?.Dispose();
            }
        }
    }

    /// <summary>One request's outcome: the command's exit code and each output stream, kept apart.</summary>
    internal readonly record struct RequestResult(int ExitCode, string Stderr, string Stdout);

    /// <summary>
    /// Reads one request and runs it. Separated from the socket handling so the caller can always
    /// produce a response, including when this throws.
    /// </summary>
    internal static async Task<RequestResult> RunRequestAsync(
        StreamReader reader, CancellationTokenSource shutdownCts)
    {
        var first = await reader.ReadLineAsync(shutdownCts.Token);
        if (string.IsNullOrWhiteSpace(first))
            return new RequestResult(1, $"error: empty command{Environment.NewLine}", "");

        // Shutdown sentinel from the `stop` command — ack, then trip the cancellation that
        // unwinds the accept loop and runs cleanup. Deliberately a bare line rather than a framed
        // request, so `stop` from any build can always shut a server down.
        if (first.Trim() == ServerClient.ShutdownRequest)
        {
            shutdownCts.Cancel();
            return new RequestResult(0, "", $"ok: shutting down{Environment.NewLine}");
        }

        if (first.Trim() != ServerClient.RequestHeader)
            return new RequestResult(1,
                $"error: unrecognized request framing; this server speaks protocol v{ServerClient.ProtocolVersion}.{Environment.NewLine}", "");

        // The client's working directory, so a relative --config/--baseline/--append resolves
        // against the caller's directory rather than wherever `reforge serve` happened to start.
        var clientWorkingDirectory = await reader.ReadLineAsync(shutdownCts.Token);
        var commandLine = await reader.ReadLineAsync(shutdownCts.Token);
        if (string.IsNullOrWhiteSpace(commandLine))
            return new RequestResult(1, $"error: empty command{Environment.NewLine}", "");

        var args = SplitCommandLine(commandLine);

        // Refuse anything the registry says this host does not serve, naming the command. The
        // client filters the same way, so reaching here means a version-skewed client or a
        // hand-written socket — either way the caller gets an error and a non-zero code rather
        // than the root help text and exit 0.
        var requested = CommandRegistry.FindCommandToken(args);
        if (requested is null || !CommandRegistry.IsRelayEligible(requested))
        {
            var name = requested ?? (args.Length == 0 ? "" : args[0]);
            return new RequestResult(1,
                $"error: '{name}' is not a command the reforge server can run.{Environment.NewLine}", "");
        }

        // Capture both streams. stderr used to go to TextWriter.Null, which threw away the only
        // actionable message some commands produce — `section-shape --section <unknown>` reports
        // the bad filter on stderr and then prints a legitimate-looking empty result on stdout.
        var originalOut = Console.Out;
        var originalErr = Console.Error;
        var capturedOut = new StringWriter();
        var capturedErr = new StringWriter();
        Console.SetOut(capturedOut);
        Console.SetError(capturedErr);

        // Adopting the client's directory is safe because the accept loop is sequential (see the
        // comment there — Roslyn is not thread-safe for mutation), so no other request observes it.
        var serverWorkingDirectory = Directory.GetCurrentDirectory();
        TrySetCurrentDirectory(clientWorkingDirectory);

        try
        {
            var solutionOption = new Option<string?>("--solution")
            {
                Recursive = true
            };
            var formatOption = new Option<OutputFormat>("--format")
            {
                DefaultValueFactory = _ => OutputFormat.Compact,
                Recursive = true
            };
            var limitOption = new Option<int?>("--limit")
            {
                Description = "Maximum number of results to return",
                Recursive = true
            };

            var rootCommand = new System.CommandLine.RootCommand("Reforge")
            {
                solutionOption,
                formatOption,
                limitOption
            };

            // The same list the cold path builds from, minus the commands that cannot run in this
            // process. Before this shared registry the server carried its own copy, last updated
            // 2026-04-13, and every command added after that date resolved to help text and exit 0.
            foreach (var command in CommandRegistry.CreateAll(
                         new CommandOptions(solutionOption, formatOption, limitOption),
                         relayEligibleOnly: true))
                rootCommand.Add(command);

            var exitCode = await rootCommand.Parse(args).InvokeAsync();
            return new RequestResult(exitCode, capturedErr.ToString(), capturedOut.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalErr);
            TrySetCurrentDirectory(serverWorkingDirectory);
        }
    }

    /// <summary>
    /// Best-effort <c>chdir</c>. A client may send a directory this process cannot enter (deleted,
    /// or on a machine-local path a container does not share); that is not worth failing the whole
    /// request over, since only relative path arguments depend on it.
    /// </summary>
    private static void TrySetCurrentDirectory(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return;
        try { Directory.SetCurrentDirectory(directory); } catch { /* keep the current directory */ }
    }

    /// <summary>
    /// Writes the framed response. The framing — exit code plus a length-prefixed stderr section —
    /// is what lets the client tell a failed command from a successful one and keep the two streams
    /// apart; see <see cref="ServerClient.FormatResponse"/> for why it is length-prefixed.
    /// </summary>
    private static async Task WriteResultAsync(StreamWriter writer, RequestResult result)
        => await writer.WriteAsync(ServerClient.FormatResponse(result.ExitCode, result.Stderr, result.Stdout));

    /// <summary>
    /// Basic command line splitting that handles quoted strings.
    /// </summary>
    private static string[] SplitCommandLine(string line)
    {
        var args = new List<string>();
        var current = new System.Text.StringBuilder();
        bool inQuotes = false;

        foreach (var c in line)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (c == ' ' && !inQuotes)
            {
                if (current.Length > 0)
                {
                    args.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(c);
            }
        }

        if (current.Length > 0)
            args.Add(current.ToString());

        return args.ToArray();
    }
}
