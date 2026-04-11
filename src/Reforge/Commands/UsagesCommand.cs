using System.CommandLine;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;

namespace Reforge.Commands;

public static class UsagesCommand
{
    public static Command Create(Option<string?> solutionOption, Option<OutputFormat> formatOption)
    {
        var typeArg = new Argument<string>("type") { Description = "The type to find usages of" };
        var inOption = new Option<string?>("--in")
        {
            Description = "Filter results to a specific namespace (prefix match)"
        };

        var command = new Command("usages", "Find where a type is used (fields, locals, params, return types, etc.)")
        {
            typeArg,
            inOption
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var solutionPath = parseResult.GetValue(solutionOption);
            var format = parseResult.GetValue(formatOption);
            var symbolQuery = parseResult.GetValue(typeArg)!;
            var namespaceFilter = parseResult.GetValue(inOption);

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
                    OutputFormatter.WriteMessage("usages", msg, format);
                    return;
                }

                if (symbols.Count > 1)
                {
                    var candidates = string.Join(", ", symbols.Select(s => s.ToDisplayString()));
                    OutputFormatter.WriteMessage("usages",
                        $"Ambiguous symbol '{symbolQuery}'. Candidates: {candidates}", format);
                    return;
                }

                if (symbols[0] is not INamedTypeSymbol typeSymbol)
                {
                    OutputFormatter.WriteMessage("usages",
                        $"'{symbolQuery}' is not a type (it is a {symbols[0].Kind}).", format);
                    return;
                }

                var solutionDir = LocationHelper.GetSolutionDirectory(solution);
                var refs = await SymbolFinder.FindReferencesAsync(typeSymbol, solution, cancellationToken);
                var entries = new List<ResultEntry>();

                foreach (var refGroup in refs)
                {
                    foreach (var refLocation in refGroup.Locations)
                    {
                        var location = refLocation.Location;
                        if (!location.IsInSource || location.SourceTree is null)
                            continue;

                        var root = await location.SourceTree.GetRootAsync(cancellationToken);
                        var node = root.FindNode(location.SourceSpan);
                        var usageKind = ClassifyUsage(node);

                        // Namespace filter: find the containing type and check its namespace
                        if (namespaceFilter is not null)
                        {
                            var containingNs = GetContainingNamespace(node, location, solution);
                            if (containingNs is null ||
                                !containingNs.StartsWith(namespaceFilter, StringComparison.OrdinalIgnoreCase))
                                continue;
                        }

                        var entry = LocationHelper.ToResultEntry(refLocation, solutionDir);
                        entries.Add(entry with
                        {
                            Context = $"[{usageKind}] {entry.Context}"
                        });
                    }
                }

                OutputFormatter.WriteResults(
                    "usages",
                    typeSymbol.ToDisplayString(),
                    entries,
                    format,
                    entry => entry);
            }
        });

        return command;
    }

    private static string ClassifyUsage(SyntaxNode node)
    {
        var current = node.Parent;
        while (current is not null)
        {
            switch (current)
            {
                case BaseListSyntax:
                    return "base type";

                case FieldDeclarationSyntax:
                    return "field";

                case PropertyDeclarationSyntax propDecl:
                    // Check if the node is in the type position (not in the body/accessor)
                    if (propDecl.Type.Span.Contains(node.Span))
                        return "property type";
                    return "reference";

                case ParameterSyntax:
                    return "parameter";

                case MethodDeclarationSyntax methodDecl:
                    // Check if the node is in the return type position
                    if (methodDecl.ReturnType.Span.Contains(node.Span))
                        return "return type";
                    return "reference";

                case LocalDeclarationStatementSyntax:
                    return "local";

                case VariableDeclarationSyntax when current.Parent is LocalDeclarationStatementSyntax:
                    return "local";

                case TypeParameterConstraintClauseSyntax:
                    return "type constraint";

                case AttributeSyntax:
                    return "attribute";

                case ObjectCreationExpressionSyntax:
                    return "instantiation";

                case CastExpressionSyntax:
                    return "cast";

                case TypeOfExpressionSyntax:
                    return "typeof";

                case IsPatternExpressionSyntax:
                    return "pattern match";

                case CatchDeclarationSyntax:
                    return "catch";
            }

            // Stop walking up if we hit a type/namespace declaration
            if (current is TypeDeclarationSyntax or NamespaceDeclarationSyntax or CompilationUnitSyntax)
                break;

            current = current.Parent;
        }

        return "reference";
    }

    private static string? GetContainingNamespace(SyntaxNode node, Location location, Solution solution)
    {
        // Walk up the syntax tree to find the containing type declaration,
        // then determine its namespace from the semantic model if possible.
        var current = node;
        while (current is not null)
        {
            if (current is TypeDeclarationSyntax typeDecl)
            {
                // Try to get the namespace from parent namespace declarations
                return GetNamespaceFromSyntax(typeDecl);
            }

            current = current.Parent;
        }

        return null;
    }

    private static string? GetNamespaceFromSyntax(SyntaxNode node)
    {
        var current = node.Parent;
        while (current is not null)
        {
            if (current is FileScopedNamespaceDeclarationSyntax fileScopedNs)
                return fileScopedNs.Name.ToString();

            if (current is NamespaceDeclarationSyntax ns)
                return ns.Name.ToString();

            current = current.Parent;
        }

        return null;
    }
}
