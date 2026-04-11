using System.CommandLine;

namespace Reforge.Commands;

public static class RequestCommand
{
    private static readonly string LogDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".reforge");
    private static readonly string LogFile = Path.Combine(LogDir, "requests.log");

    public static Command Create()
    {
        var descriptionArg = new Argument<string?>("description")
        {
            Description = "Description of the feature request",
            Arity = ArgumentArity.ZeroOrOne
        };

        var listOption = new Option<bool>("--list")
        {
            Description = "Display all logged feature requests"
        };

        var clearOption = new Option<bool>("--clear")
        {
            Description = "Clear all logged feature requests"
        };

        var command = new Command("request", "Log a feature request for future Reforge improvements")
        {
            descriptionArg,
            listOption,
            clearOption
        };

        command.SetAction((parseResult, cancellationToken) =>
        {
            var list = parseResult.GetValue(listOption);
            var clear = parseResult.GetValue(clearOption);
            var description = parseResult.GetValue(descriptionArg);

            if (clear)
            {
                if (File.Exists(LogFile))
                {
                    File.WriteAllText(LogFile, string.Empty);
                    Console.WriteLine("Cleared requests log.");
                }
                else
                {
                    Console.WriteLine("No requests log to clear.");
                }
                return Task.CompletedTask;
            }

            if (list)
            {
                if (File.Exists(LogFile))
                {
                    var content = File.ReadAllText(LogFile);
                    if (string.IsNullOrWhiteSpace(content))
                        Console.WriteLine("No requests logged.");
                    else
                        Console.Write(content);
                }
                else
                {
                    Console.WriteLine("No requests logged.");
                }
                return Task.CompletedTask;
            }

            if (string.IsNullOrWhiteSpace(description))
            {
                Console.WriteLine("Usage: reforge request \"description of what you needed\"");
                Console.WriteLine("       reforge request --list");
                Console.WriteLine("       reforge request --clear");
                return Task.CompletedTask;
            }

            try
            {
                Directory.CreateDirectory(LogDir);
                var entry = $"[{DateTime.UtcNow:O}] {description}";
                File.AppendAllText(LogFile, entry + Environment.NewLine);
                Console.WriteLine($"Logged request to ~/.reforge/requests.log");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Failed to log request: {ex.Message}");
            }

            return Task.CompletedTask;
        });

        return command;
    }
}
