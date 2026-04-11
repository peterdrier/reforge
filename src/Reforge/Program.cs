using System.CommandLine;
using Microsoft.Build.Locator;
using Reforge.Commands;

// Register MSBuild BEFORE any Roslyn types are loaded.
// This must happen before anything touches Microsoft.CodeAnalysis.
MSBuildLocator.RegisterDefaults();

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
        Description = "Output format (json or table)",
        DefaultValueFactory = _ => Reforge.OutputFormat.Json,
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

    // Help
    rootCommand.Add(SkillCommand.Create());

    // Phase 2 — Mechanical Transform commands (future)

    var parseResult = rootCommand.Parse(args);
    return await parseResult.InvokeAsync();
}
