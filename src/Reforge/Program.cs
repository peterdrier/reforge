using System.CommandLine;
using Reforge;

// Try relaying to a hot server FIRST — before MSBuildLocator or any Roslyn types load.
// ServerClient is pure TCP, no Roslyn dependency. This skips the expensive startup path.
//
// Only a relay-eligible command is forwarded. An unknown name is deliberately NOT relayed: it has
// to reach the cold path so System.CommandLine can report an unrecognized command with a non-zero
// exit, rather than the server answering with help text and exit 0. CommandRegistry.Specs holds
// only strings and bools precisely so this check loads neither System.CommandLine nor Roslyn.
//
// The whole argument list is inspected, not args[0]: the root's --solution/--format/--limit are
// recursive, so `reforge --format json cycles` is valid and its first argument is an option.
if (args.Length > 0 && CommandRegistry.IsRelayEligible(args))
{
    // null means no server is reachable — fall through to the cold path. Any other value is the
    // exit code of the command the server actually ran, propagated rather than flattened to 0.
    var relayedExitCode = await ServerClient.TryRelayAsync(args);
    if (relayedExitCode is not null)
        return relayedExitCode.Value;
}

// Cold path: register MSBuild BEFORE any Roslyn types are loaded.
Microsoft.Build.Locator.MSBuildLocator.RegisterDefaults();

return await RunAsync(args);

// Separate method so MSBuildLocator registration completes before Roslyn types are JIT'd.
static async Task<int> RunAsync(string[] args)
{
    var solutionOption = new Option<string?>("--solution")
    {
        Description = "Path to solution file (.slnx or .sln). If omitted, searches upward from CWD.",
        Recursive = true
    };

    var formatOption = new Option<Reforge.OutputFormat>("--format")
    {
        Description = "Output format (compact or json)",
        DefaultValueFactory = _ => Reforge.OutputFormat.Compact,
        Recursive = true
    };

    var limitOption = new Option<int?>("--limit")
    {
        Description = "Maximum number of results to return",
        Recursive = true
    };

    var rootCommand = new RootCommand("Reforge — Roslyn-powered semantic query and refactoring CLI for AI coding assistants")
    {
        solutionOption,
        formatOption,
        limitOption
    };

    // One list, shared with the hot server (ServeCommand). The cold path registers every command;
    // the server registers only the relay-eligible ones. Phase 2 — Mechanical Transform commands
    // will be added to CommandRegistry, not here.
    foreach (var command in CommandRegistry.CreateAll(new CommandOptions(solutionOption, formatOption, limitOption)))
        rootCommand.Add(command);

    var parseResult = rootCommand.Parse(args);
    return await parseResult.InvokeAsync();
}
