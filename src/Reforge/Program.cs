using System.CommandLine;
using Reforge;
using Reforge.Commands;

// Try relaying to a hot server FIRST — before MSBuildLocator or any Roslyn types load.
// ServerClient is pure TCP, no Roslyn dependency. This skips the expensive startup path.
if (args.Length > 0 && args[0] is not "serve" and not "skill" and not "install" and not "--help" and not "-h")
{
    if (await ServerClient.TryRelayAsync(args))
        return 0;
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

    var rootCommand = new RootCommand("Reforge — Roslyn-powered semantic query and refactoring CLI for AI coding assistants")
    {
        solutionOption,
        formatOption
    };

    // Phase 1 — Semantic Query commands
    rootCommand.Add(ReferencesCommand.Create(solutionOption, formatOption));
    rootCommand.Add(CallersCommand.Create(solutionOption, formatOption));
    rootCommand.Add(ImplementationsCommand.Create(solutionOption, formatOption));
    rootCommand.Add(MembersCommand.Create(solutionOption, formatOption));
    rootCommand.Add(DependenciesCommand.Create(solutionOption, formatOption));
    rootCommand.Add(InjectedCommand.Create(solutionOption, formatOption));
    rootCommand.Add(InheritorsCommand.Create(solutionOption, formatOption));
    rootCommand.Add(CallChainCommand.Create(solutionOption, formatOption));
    rootCommand.Add(UsagesCommand.Create(solutionOption, formatOption));
    rootCommand.Add(ParametersCommand.Create(solutionOption, formatOption));

    // Help & setup
    rootCommand.Add(SkillCommand.Create());
    rootCommand.Add(InstallCommand.Create());

    // Server
    rootCommand.Add(ServeCommand.Create(solutionOption));

    // Phase 2 — Mechanical Transform commands (future)

    var parseResult = rootCommand.Parse(args);
    return await parseResult.InvokeAsync();
}
