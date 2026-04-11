using System.CommandLine;
using Microsoft.CodeAnalysis;

namespace Reforge.Commands;

public static class DependenciesCommand
{
    public static Command Create(Option<string?> solutionOption, Option<OutputFormat> formatOption)
    {
        var symbolArg = new Argument<string>("class") { Description = "The class to analyze dependencies for" };
        var command = new Command("dependencies", "Show what types a class depends on (constructor params, fields, properties)") { symbolArg };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var solutionPath = parseResult.GetValue(solutionOption);
            var format = parseResult.GetValue(formatOption);
            var symbolQuery = parseResult.GetValue(symbolArg)!;

            var (solution, handle) = await WorkspaceHelper.OpenSolutionAsync(solutionPath);
            using (handle)
            {
                var symbols = await SymbolResolver.ResolveAsync(solution, symbolQuery);
                if (symbols.Count == 0)
                {
                    var suggestions = await SymbolResolver.SuggestAsync(solution, symbolQuery);
                    var msg = suggestions.Count > 0
                        ? $"Symbol '{symbolQuery}' not found. Did you mean: {string.Join(", ", suggestions)}"
                        : $"Symbol '{symbolQuery}' not found.";
                    OutputFormatter.WriteMessage("dependencies", msg, format);
                    return;
                }

                if (symbols.Count > 1)
                {
                    var candidates = string.Join(", ", symbols.Select(s => s.ToDisplayString()));
                    OutputFormatter.WriteMessage("dependencies",
                        $"Ambiguous symbol '{symbolQuery}'. Candidates: {candidates}", format);
                    return;
                }

                if (symbols[0] is not INamedTypeSymbol typeSymbol)
                {
                    OutputFormatter.WriteMessage("dependencies",
                        $"'{symbolQuery}' is not a type (it is a {symbols[0].Kind}).", format);
                    return;
                }

                var solutionDir = LocationHelper.GetSolutionDirectory(solution);
                var entries = CollectDependencies(typeSymbol, solutionDir);

                OutputFormatter.WriteResults(
                    "dependencies",
                    typeSymbol.ToDisplayString(),
                    entries,
                    format,
                    entry => entry);
            }
        });

        return command;
    }

    private static List<ResultEntry> CollectDependencies(INamedTypeSymbol typeSymbol, string solutionDir)
    {
        var entries = new List<ResultEntry>();
        var classFile = GetRelativeFilePath(typeSymbol, solutionDir);

        // 1. Constructor parameters
        foreach (var ctor in typeSymbol.Constructors)
        {
            if (ctor.IsImplicitlyDeclared)
                continue;

            foreach (var param in ctor.Parameters)
            {
                var paramType = param.Type;
                if (IsSystemPrimitive(paramType))
                    continue;

                var line = GetSourceLine(param);
                entries.Add(new ResultEntry(
                    classFile,
                    line,
                    1,
                    $"constructor parameter: {paramType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)} {param.Name}",
                    typeSymbol.Name));
            }
        }

        // 2. Fields
        foreach (var member in typeSymbol.GetMembers().OfType<IFieldSymbol>())
        {
            if (member.IsImplicitlyDeclared)
                continue;

            var fieldType = member.Type;
            if (IsSystemPrimitive(fieldType))
                continue;

            var modifiers = GetFieldModifiers(member);
            var line = GetSourceLine(member);
            entries.Add(new ResultEntry(
                classFile,
                line,
                1,
                $"{modifiers}field: {fieldType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)} {member.Name}",
                typeSymbol.Name));
        }

        // 3. Properties
        foreach (var member in typeSymbol.GetMembers().OfType<IPropertySymbol>())
        {
            if (member.IsImplicitlyDeclared)
                continue;

            var propType = member.Type;
            if (IsSystemPrimitive(propType))
                continue;

            var line = GetSourceLine(member);
            entries.Add(new ResultEntry(
                classFile,
                line,
                1,
                $"property: {propType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)} {member.Name}",
                typeSymbol.Name));
        }

        return entries;
    }

    private static string GetFieldModifiers(IFieldSymbol field)
    {
        var parts = new List<string>();
        if (field.IsStatic) parts.Add("static ");
        if (field.IsReadOnly) parts.Add("readonly ");
        if (field.DeclaredAccessibility == Accessibility.Private) parts.Add("private ");
        else if (field.DeclaredAccessibility == Accessibility.Public) parts.Add("public ");
        else if (field.DeclaredAccessibility == Accessibility.Internal) parts.Add("internal ");
        else if (field.DeclaredAccessibility == Accessibility.Protected) parts.Add("protected ");
        return string.Join("", parts);
    }

    private static bool IsSystemPrimitive(ITypeSymbol type)
    {
        // Filter out primitive types that aren't interesting as dependencies
        return type.SpecialType switch
        {
            SpecialType.System_Boolean => true,
            SpecialType.System_Byte => true,
            SpecialType.System_SByte => true,
            SpecialType.System_Int16 => true,
            SpecialType.System_UInt16 => true,
            SpecialType.System_Int32 => true,
            SpecialType.System_UInt32 => true,
            SpecialType.System_Int64 => true,
            SpecialType.System_UInt64 => true,
            SpecialType.System_Single => true,
            SpecialType.System_Double => true,
            SpecialType.System_Decimal => true,
            SpecialType.System_Char => true,
            SpecialType.System_String => true,
            SpecialType.System_Object => true,
            SpecialType.System_Void => true,
            _ => false
        };
    }

    private static int GetSourceLine(ISymbol symbol)
    {
        var location = symbol.Locations.FirstOrDefault(l => l.IsInSource);
        if (location is null)
            return 0;

        return location.GetLineSpan().StartLinePosition.Line + 1; // 0-based to 1-based
    }

    private static string GetRelativeFilePath(ISymbol symbol, string solutionDir)
    {
        var location = symbol.Locations.FirstOrDefault(l => l.IsInSource);
        if (location is null)
            return string.Empty;

        var absolutePath = location.GetLineSpan().Path;
        return LocationHelper.NormalizePath(absolutePath, solutionDir);
    }
}
