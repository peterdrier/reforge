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

            // Write port file
            await File.WriteAllTextAsync(portFile, actualPort.ToString(), cancellationToken);

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

                var (exitCode, output) = await RunRequestAsync(reader, shutdownCts);
                await WriteResultAsync(writer, exitCode, output);
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
                        await WriteResultAsync(writer, 1, $"error: {ex.Message}{Environment.NewLine}");
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

    /// <summary>
    /// Reads one request and runs it, returning the exit code and whatever the command wrote to
    /// stdout. Separated from the socket handling so the caller can always produce a response,
    /// including when this throws.
    /// </summary>
    internal static async Task<(int ExitCode, string Output)> RunRequestAsync(
        StreamReader reader, CancellationTokenSource shutdownCts)
    {
        var commandLine = await reader.ReadLineAsync(shutdownCts.Token);
        if (string.IsNullOrWhiteSpace(commandLine))
            return (1, $"error: empty command{Environment.NewLine}");

        // Shutdown sentinel from the `stop` command — ack, then trip the
        // cancellation that unwinds the accept loop and runs cleanup.
        if (commandLine.Trim() == "__shutdown__")
        {
            shutdownCts.Cancel();
            return (0, $"ok: shutting down{Environment.NewLine}");
        }

        var args = SplitCommandLine(commandLine);

        // Refuse anything the registry says this host does not serve, naming the command. The
        // client filters the same way, so reaching here means a version-skewed client or a
        // hand-written socket — either way the caller gets an error and a non-zero code rather
        // than the root help text and exit 0.
        if (args.Length == 0 || !CommandRegistry.IsRelayEligible(args[0]))
        {
            var name = args.Length == 0 ? "" : args[0];
            return (1, $"error: '{name}' is not a command the reforge server can run.{Environment.NewLine}");
        }

        // Capture stdout during command execution
        var originalOut = Console.Out;
        var originalErr = Console.Error;
        var captured = new StringWriter();
        Console.SetOut(captured);
        Console.SetError(TextWriter.Null); // suppress workspace diagnostics for relayed commands

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
            return (exitCode, captured.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalErr);
        }
    }

    /// <summary>
    /// Writes the exit-code status line followed by the command's output. The status line is what
    /// lets the client distinguish a failed command from a successful one — see
    /// <see cref="ServerClient.ExitCodeSentinel"/>.
    /// </summary>
    private static async Task WriteResultAsync(StreamWriter writer, int exitCode, string output)
    {
        await writer.WriteAsync($"{ServerClient.ExitCodeSentinel}{exitCode}\n");
        await writer.WriteAsync(output);
    }

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
