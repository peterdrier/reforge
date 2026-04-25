using System.CommandLine;
using Reforge;
using Reforge.Commands;

// Try relaying to a hot server FIRST — before MSBuildLocator or any Roslyn types load.
// ServerClient is pure TCP, no Roslyn dependency. This skips the expensive startup path.
if (args.Length > 0 && args[0] is not "serve" and not "skill" and not "install" and not "request" and not "--list" and not "--help" and not "-h")
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

    // Phase 1 — Semantic Query commands
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

    // Service ownership analysis
    rootCommand.Add(DbSetUsageCommand.Create(solutionOption, formatOption, limitOption));
    rootCommand.Add(OwnershipViolationsCommand.Create(solutionOption, formatOption, limitOption));
    rootCommand.Add(ServiceMapCommand.Create(solutionOption, formatOption, limitOption));

    // Code health analysis
    rootCommand.Add(HealthCommand.Create(solutionOption, formatOption, limitOption));
    rootCommand.Add(SnapshotCommand.Create(solutionOption, formatOption, limitOption));
    rootCommand.Add(CyclesCommand.Create(solutionOption, formatOption, limitOption));

    // Audit commands
    rootCommand.Add(AuditAuthCommand.Create(solutionOption, formatOption, limitOption));
    rootCommand.Add(AuditCacheCommand.Create(solutionOption, formatOption, limitOption));
    rootCommand.Add(AuditImmutableCommand.Create(solutionOption, formatOption, limitOption));
    rootCommand.Add(AuditEfCommand.Create(solutionOption, formatOption, limitOption));
    rootCommand.Add(AuditSurfaceCommand.Create(solutionOption, formatOption, limitOption));
    rootCommand.Add(AuditDownstreamCommand.Create(solutionOption, formatOption, limitOption));

    // Help & setup
    rootCommand.Add(SkillCommand.Create());
    rootCommand.Add(InstallCommand.Create());
    rootCommand.Add(RequestCommand.Create());

    // Server
    rootCommand.Add(ServeCommand.Create(solutionOption));

    // Phase 2 — Mechanical Transform commands (future)

    var parseResult = rootCommand.Parse(args);
    return await parseResult.InvokeAsync();
}
