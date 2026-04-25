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

        var command = new Command("serve", "Start hot workspace server for fast repeated queries")
        {
            portOption
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

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    TcpClient client;
                    try
                    {
                        client = await listener.AcceptTcpClientAsync(cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    // Process clients sequentially — Roslyn isn't thread-safe for mutations
                    await HandleClientAsync(client, cancellationToken);
                }
            }
            finally
            {
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

    private static async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        try
        {
            using (client)
            {
                var stream = client.GetStream();
                using var reader = new StreamReader(stream);
                using var writer = new StreamWriter(stream) { AutoFlush = true };

                // Read command line (single line)
                var commandLine = await reader.ReadLineAsync(ct);
                if (string.IsNullOrWhiteSpace(commandLine))
                {
                    await writer.WriteLineAsync("error: empty command");
                    return;
                }

                // Capture stdout during command execution
                var originalOut = Console.Out;
                var originalErr = Console.Error;
                var sw = new StringWriter();
                Console.SetOut(sw);
                Console.SetError(TextWriter.Null); // suppress workspace diagnostics for relayed commands

                try
                {
                    // Split command line into args
                    var args = SplitCommandLine(commandLine);

                    // Build the same root command setup
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

                    rootCommand.Add(ReferencesCommand.Create(solutionOption, formatOption, limitOption));
                    rootCommand.Add(CallersCommand.Create(solutionOption, formatOption, limitOption));
                    rootCommand.Add(ImplementationsCommand.Create(solutionOption, formatOption, limitOption));
                    rootCommand.Add(MembersCommand.Create(solutionOption, formatOption, limitOption));
                    rootCommand.Add(DependenciesCommand.Create(solutionOption, formatOption, limitOption));
                    rootCommand.Add(InjectedCommand.Create(solutionOption, formatOption, limitOption));
                    rootCommand.Add(InheritorsCommand.Create(solutionOption, formatOption, limitOption));
                    rootCommand.Add(CallChainCommand.Create(solutionOption, formatOption, limitOption));
                    rootCommand.Add(UsagesCommand.Create(solutionOption, formatOption, limitOption));
                    rootCommand.Add(ParametersCommand.Create(solutionOption, formatOption, limitOption));
                    rootCommand.Add(DbSetUsageCommand.Create(solutionOption, formatOption, limitOption));
                    rootCommand.Add(OwnershipViolationsCommand.Create(solutionOption, formatOption, limitOption));
                    rootCommand.Add(ServiceMapCommand.Create(solutionOption, formatOption, limitOption));
                    rootCommand.Add(HealthCommand.Create(solutionOption, formatOption, limitOption));
                    rootCommand.Add(AuditAuthCommand.Create(solutionOption, formatOption, limitOption));
                    rootCommand.Add(AuditCacheCommand.Create(solutionOption, formatOption, limitOption));
                    rootCommand.Add(AuditImmutableCommand.Create(solutionOption, formatOption, limitOption));
                    rootCommand.Add(AuditEfCommand.Create(solutionOption, formatOption, limitOption));
                    rootCommand.Add(AuditSurfaceCommand.Create(solutionOption, formatOption, limitOption));
                    rootCommand.Add(AuditDownstreamCommand.Create(solutionOption, formatOption, limitOption));
                    rootCommand.Add(SkillCommand.Create());

                    var parseResult = rootCommand.Parse(args);
                    await parseResult.InvokeAsync();
                }
                finally
                {
                    Console.SetOut(originalOut);
                    Console.SetError(originalErr);
                }

                // Send captured output back
                await writer.WriteAsync(sw.ToString());
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Client error: {ex.Message}");
        }
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
