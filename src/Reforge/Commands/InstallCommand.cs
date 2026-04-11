using System.CommandLine;

namespace Reforge.Commands;

public static class InstallCommand
{
    private const string SkillContent = """
        ---
        name: reforge
        description: Roslyn-powered semantic query CLI for C# solutions. Use when you need to find references, callers, implementations, dependencies, members, or trace call chains in a C# codebase — replaces multi-round grep/read cycles with single precise queries.
        ---

        Run `reforge skill` to get the full usage guide:

        ```bash
        reforge skill
        ```

        Follow the output as your reference for which command to use and how to interpret results.

        For large solutions, start a hot server first to avoid the cold start tax:

        ```bash
        reforge serve --solution path/to/Solution.slnx &
        ```

        Then all subsequent `reforge` commands auto-relay to the server (~200ms instead of 3-20s).
        """;

    public static Command Create()
    {
        var command = new Command("install", "Install the reforge skill into Claude Code's global skills directory");

        command.SetAction((parseResult, cancellationToken) =>
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var skillDir = Path.Combine(home, ".claude", "skills", "reforge");
            var skillFile = Path.Combine(skillDir, "SKILL.md");

            Directory.CreateDirectory(skillDir);
            File.WriteAllText(skillFile, SkillContent);

            // Clean up old flat file if it exists
            var oldFile = Path.Combine(home, ".claude", "skills", "reforge.md");
            if (File.Exists(oldFile)) File.Delete(oldFile);

            Console.WriteLine($"Installed skill to {skillFile}");
            Console.WriteLine("Reforge is now available as a skill in all Claude Code sessions.");

            return Task.CompletedTask;
        });

        return command;
    }
}
