using System.CommandLine;
using Microsoft.CodeAnalysis;

namespace Reforge.Commands;

public static class MembersCommand
{
    public static Command Create(Option<string?> solutionOption, Option<OutputFormat> formatOption)
    {
        var typeArg = new Argument<string>("type") { Description = "The type to list members of" };
        var command = new Command("members", "List members of a type with types, visibility, and modifiers") { typeArg };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var solutionPath = parseResult.GetValue(solutionOption);
            var format = parseResult.GetValue(formatOption);
            var symbolQuery = parseResult.GetValue(typeArg)!;

            var (solution, workspace) = await WorkspaceHelper.OpenSolutionAsync(solutionPath);
            using (workspace)
            {
                var symbols = await SymbolResolver.ResolveAsync(solution, symbolQuery);
                if (symbols.Count == 0)
                {
                    OutputFormatter.WriteMessage("members", $"Symbol '{symbolQuery}' not found.", format);
                    return;
                }

                if (symbols.Count > 1)
                {
                    // Filter to types only — if exactly one is a type, use it
                    var types = symbols.OfType<INamedTypeSymbol>().ToList();
                    if (types.Count == 1)
                    {
                        symbols = [types[0]];
                    }
                    else
                    {
                        var candidates = string.Join(", ", symbols.Select(s => s.ToDisplayString()));
                        OutputFormatter.WriteMessage("members",
                            $"Ambiguous symbol '{symbolQuery}'. Candidates: {candidates}", format);
                        return;
                    }
                }

                var symbol = symbols[0];
                if (symbol is not INamedTypeSymbol typeSymbol)
                {
                    OutputFormatter.WriteMessage("members",
                        $"Symbol '{symbolQuery}' is not a type (it is a {symbol.Kind}).", format);
                    return;
                }

                var solutionDir = LocationHelper.GetSolutionDirectory(solution);

                var members = typeSymbol.GetMembers()
                    .Where(m => !m.IsImplicitlyDeclared)
                    .Where(m => m.Locations.Length > 0 && m.Locations[0].IsInSource)
                    .ToList();

                OutputFormatter.WriteResults(
                    "members",
                    typeSymbol.ToDisplayString(),
                    members,
                    format,
                    member =>
                    {
                        var location = member.Locations[0];
                        var lineSpan = location.GetLineSpan();
                        var filePath = MakeRelativePath(lineSpan.Path, solutionDir);
                        var line = lineSpan.StartLinePosition.Line + 1;
                        var column = lineSpan.StartLinePosition.Character + 1;
                        var signature = FormatMemberSignature(member);

                        return new ResultEntry(filePath, line, column, signature, typeSymbol.Name);
                    });
            }
        });

        return command;
    }

    private static string FormatMemberSignature(ISymbol member)
    {
        var accessibility = member.DeclaredAccessibility switch
        {
            Accessibility.Public => "public",
            Accessibility.Private => "private",
            Accessibility.Protected => "protected",
            Accessibility.Internal => "internal",
            Accessibility.ProtectedOrInternal => "protected internal",
            Accessibility.ProtectedAndInternal => "private protected",
            _ => ""
        };

        var modifiers = new List<string>();
        if (!string.IsNullOrEmpty(accessibility))
            modifiers.Add(accessibility);
        if (member.IsStatic)
            modifiers.Add("static");
        if (member.IsAbstract)
            modifiers.Add("abstract");
        if (member.IsVirtual)
            modifiers.Add("virtual");
        if (member.IsOverride)
            modifiers.Add("override");
        if (member.IsSealed && member.IsOverride) // sealed only meaningful with override
            modifiers.Insert(modifiers.IndexOf("override"), "sealed");

        var display = member.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);

        // If the display already starts with the accessibility, use it as-is
        if (!string.IsNullOrEmpty(accessibility) && display.StartsWith(accessibility))
            return display;

        // Prepend modifiers to the minimally qualified display
        var prefix = string.Join(" ", modifiers);
        return string.IsNullOrEmpty(prefix) ? display : $"{prefix} {display}";
    }

    private static string MakeRelativePath(string absolutePath, string solutionDirectory)
    {
        if (string.IsNullOrEmpty(absolutePath))
            return absolutePath;

        if (absolutePath.StartsWith(solutionDirectory, StringComparison.OrdinalIgnoreCase))
        {
            var relative = absolutePath[solutionDirectory.Length..];
            if (relative.Length > 0 && (relative[0] == '\\' || relative[0] == '/'))
                relative = relative[1..];
            return relative.Replace('\\', '/');
        }

        return absolutePath.Replace('\\', '/');
    }
}
