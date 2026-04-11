using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;

namespace Reforge;

/// <summary>
/// Converts Roslyn Location and ReferenceLocation objects into ResultEntry records
/// suitable for output. File paths are made relative to the solution directory and
/// use forward slashes.
/// </summary>
public static class LocationHelper
{
    /// <summary>
    /// Converts a Roslyn Location into a ResultEntry.
    /// </summary>
    /// <param name="location">The source location (must be in source, not metadata).</param>
    /// <param name="containingSymbol">The symbol that contains this location (for context).</param>
    /// <param name="solutionDirectory">The solution root directory, used to produce relative paths.</param>
    public static ResultEntry ToResultEntry(Location location, ISymbol containingSymbol, string solutionDirectory)
    {
        var lineSpan = location.GetLineSpan();
        var filePath = NormalizePath(lineSpan.Path, solutionDirectory);
        var line = lineSpan.StartLinePosition.Line + 1; // 0-based to 1-based
        var column = lineSpan.StartLinePosition.Character + 1;
        var context = GetSourceLineText(location);
        var containingName = GetContainingName(containingSymbol);

        return new ResultEntry(filePath, line, column, context, containingName);
    }

    /// <summary>
    /// Converts a ReferenceLocation (from FindReferencesAsync) into a ResultEntry.
    /// </summary>
    /// <param name="refLocation">The reference location.</param>
    /// <param name="solutionDirectory">The solution root directory, used to produce relative paths.</param>
    public static ResultEntry ToResultEntry(ReferenceLocation refLocation, string solutionDirectory)
    {
        var location = refLocation.Location;
        var lineSpan = location.GetLineSpan();
        var filePath = NormalizePath(lineSpan.Path, solutionDirectory);
        var line = lineSpan.StartLinePosition.Line + 1;
        var column = lineSpan.StartLinePosition.Character + 1;
        var context = GetSourceLineText(location);

        // Walk up the syntax tree to find the containing symbol name
        var containingName = GetContainingNameFromLocation(location);

        return new ResultEntry(filePath, line, column, context, containingName);
    }

    /// <summary>
    /// Gets the directory containing the solution file from a Solution object.
    /// </summary>
    public static string GetSolutionDirectory(Microsoft.CodeAnalysis.Solution solution)
    {
        if (solution.FilePath is not null)
            return Path.GetDirectoryName(solution.FilePath)!;

        // Fallback: use CWD
        return Directory.GetCurrentDirectory();
    }

    /// <summary>
    /// Makes a file path relative to the solution directory and uses forward slashes.
    /// </summary>
    private static string NormalizePath(string absolutePath, string solutionDirectory)
    {
        if (string.IsNullOrEmpty(absolutePath))
            return absolutePath;

        // Make relative
        if (absolutePath.StartsWith(solutionDirectory, StringComparison.OrdinalIgnoreCase))
        {
            var relative = absolutePath[solutionDirectory.Length..];
            if (relative.StartsWith(Path.DirectorySeparatorChar) || relative.StartsWith(Path.AltDirectorySeparatorChar))
                relative = relative[1..];
            return relative.Replace('\\', '/');
        }

        // If not under solution dir, return as-is with forward slashes
        return absolutePath.Replace('\\', '/');
    }

    /// <summary>
    /// Extracts the text of the source line at the given location.
    /// </summary>
    private static string GetSourceLineText(Location location)
    {
        if (location.SourceTree is null)
            return string.Empty;

        var text = location.SourceTree.GetText();
        var lineNumber = location.GetLineSpan().StartLinePosition.Line;
        if (lineNumber < 0 || lineNumber >= text.Lines.Count)
            return string.Empty;

        return text.Lines[lineNumber].ToString().Trim();
    }

    /// <summary>
    /// Gets the best display name for a containing symbol.
    /// For types, returns the type name. For members, returns "Type.Member".
    /// </summary>
    private static string GetContainingName(ISymbol symbol)
    {
        if (symbol.ContainingType is not null)
            return $"{symbol.ContainingType.Name}.{symbol.Name}";
        return symbol.Name;
    }

    /// <summary>
    /// Attempts to determine the containing symbol name from a source location
    /// by walking up the syntax tree to find the nearest type or member declaration.
    /// Falls back to the file name if no containing symbol can be determined.
    /// </summary>
    private static string GetContainingNameFromLocation(Location location)
    {
        if (location.SourceTree is null)
            return string.Empty;

        var root = location.SourceTree.GetRoot();
        var node = root.FindNode(location.SourceSpan);

        // Walk up to find the nearest member or type declaration
        var current = node;
        while (current is not null)
        {
            // Check for common declaration kinds by their syntax kind name
            // (avoiding direct CSharp dependency to keep this general)
            var kindName = current.GetType().Name;
            if (kindName.Contains("MethodDeclaration") ||
                kindName.Contains("PropertyDeclaration") ||
                kindName.Contains("ConstructorDeclaration") ||
                kindName.Contains("FieldDeclaration"))
            {
                // Found a member — now find its parent type
                var parentType = FindParentTypeDeclaration(current.Parent);
                var memberName = GetDeclarationName(current);
                if (parentType is not null)
                {
                    var typeName = GetDeclarationName(parentType);
                    return string.IsNullOrEmpty(memberName) ? typeName : $"{typeName}.{memberName}";
                }
                return memberName;
            }

            if (kindName.Contains("ClassDeclaration") ||
                kindName.Contains("StructDeclaration") ||
                kindName.Contains("RecordDeclaration") ||
                kindName.Contains("InterfaceDeclaration") ||
                kindName.Contains("EnumDeclaration"))
            {
                return GetDeclarationName(current);
            }

            current = current.Parent;
        }

        return string.Empty;
    }

    private static Microsoft.CodeAnalysis.SyntaxNode? FindParentTypeDeclaration(Microsoft.CodeAnalysis.SyntaxNode? node)
    {
        var current = node;
        while (current is not null)
        {
            var kindName = current.GetType().Name;
            if (kindName.Contains("ClassDeclaration") ||
                kindName.Contains("StructDeclaration") ||
                kindName.Contains("RecordDeclaration") ||
                kindName.Contains("InterfaceDeclaration"))
            {
                return current;
            }
            current = current.Parent;
        }
        return null;
    }

    /// <summary>
    /// Extracts the identifier name from a declaration syntax node.
    /// Works by looking for the Identifier property via reflection to avoid
    /// coupling to specific CSharp syntax types.
    /// </summary>
    private static string GetDeclarationName(Microsoft.CodeAnalysis.SyntaxNode node)
    {
        var identifierProp = node.GetType().GetProperty("Identifier");
        if (identifierProp is not null)
        {
            var token = identifierProp.GetValue(node);
            return token?.ToString() ?? string.Empty;
        }
        return string.Empty;
    }
}
