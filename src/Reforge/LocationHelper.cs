using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
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
    public static string NormalizePath(string absolutePath, string solutionDirectory)
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
    /// Falls back to empty string if no containing symbol can be determined.
    /// </summary>
    private static string GetContainingNameFromLocation(Location location)
    {
        if (location.SourceTree is null)
            return string.Empty;

        var root = location.SourceTree.GetRoot();
        var node = root.FindNode(location.SourceSpan);

        var current = node;
        while (current is not null)
        {
            switch (current)
            {
                case MethodDeclarationSyntax method:
                    var parentType = FindParentTypeDeclaration(current.Parent);
                    return parentType is not null ? $"{GetIdentifier(parentType)}.{method.Identifier.Text}" : method.Identifier.Text;
                case ConstructorDeclarationSyntax ctor:
                    parentType = FindParentTypeDeclaration(current.Parent);
                    return parentType is not null ? $"{GetIdentifier(parentType)}.{ctor.Identifier.Text}" : ctor.Identifier.Text;
                case PropertyDeclarationSyntax prop:
                    parentType = FindParentTypeDeclaration(current.Parent);
                    return parentType is not null ? $"{GetIdentifier(parentType)}.{prop.Identifier.Text}" : prop.Identifier.Text;
                case FieldDeclarationSyntax:
                    parentType = FindParentTypeDeclaration(current.Parent);
                    return parentType is not null ? GetIdentifier(parentType) : string.Empty;
                case ClassDeclarationSyntax cls:
                    return cls.Identifier.Text;
                case StructDeclarationSyntax str:
                    return str.Identifier.Text;
                case RecordDeclarationSyntax rec:
                    return rec.Identifier.Text;
                case InterfaceDeclarationSyntax iface:
                    return iface.Identifier.Text;
                case EnumDeclarationSyntax enm:
                    return enm.Identifier.Text;
            }
            current = current.Parent;
        }
        return string.Empty;
    }

    private static SyntaxNode? FindParentTypeDeclaration(SyntaxNode? node)
    {
        var current = node;
        while (current is not null)
        {
            if (current is ClassDeclarationSyntax or StructDeclarationSyntax
                or RecordDeclarationSyntax or InterfaceDeclarationSyntax)
                return current;
            current = current.Parent;
        }
        return null;
    }

    private static string GetIdentifier(SyntaxNode node) => node switch
    {
        ClassDeclarationSyntax c => c.Identifier.Text,
        StructDeclarationSyntax s => s.Identifier.Text,
        RecordDeclarationSyntax r => r.Identifier.Text,
        InterfaceDeclarationSyntax i => i.Identifier.Text,
        _ => string.Empty
    };
}
