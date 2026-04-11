using System.CommandLine;
using System.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Reforge.Commands;

public static class AuditImmutableCommand
{
    public static Command Create(Option<string?> solutionOption, Option<OutputFormat> formatOption, Option<int?> limitOption)
    {
        var typesOption = new Option<string>("--types")
        {
            Description = "Comma-separated entity type names that should be append-only",
        };
        typesOption.Required = true;

        var command = new Command("audit-immutable", "Find mutation operations on append-only entities")
        {
            typesOption
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var solutionPath = parseResult.GetValue(solutionOption);
            var format = parseResult.GetValue(formatOption);
            var limit = parseResult.GetValue(limitOption);
            var typesRaw = parseResult.GetValue(typesOption)!;
            var sw = Stopwatch.StartNew();

            var protectedNames = typesRaw
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet(StringComparer.Ordinal);

            var (solution, handle) = await WorkspaceHelper.OpenSolutionAsync(solutionPath);
            using (handle)
            {
                var solutionDir = LocationHelper.GetSolutionDirectory(solution);
                var entries = new List<ResultEntry>();

                var mutatingMethods = new HashSet<string>(StringComparer.Ordinal)
                {
                    "Remove", "RemoveRange", "Update"
                };
                var bulkMutatingMethods = new HashSet<string>(StringComparer.Ordinal)
                {
                    "ExecuteUpdate", "ExecuteDelete", "ExecuteUpdateAsync", "ExecuteDeleteAsync"
                };

                foreach (var project in solution.Projects)
                {
                    if (project.Name.Contains("Test", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var compilation = await project.GetCompilationAsync(cancellationToken);
                    if (compilation is null)
                        continue;

                    foreach (var document in project.Documents)
                    {
                        var tree = await document.GetSyntaxTreeAsync(cancellationToken);
                        if (tree is null)
                            continue;

                        var root = await tree.GetRootAsync(cancellationToken);
                        var semanticModel = compilation.GetSemanticModel(tree);

                        // 1. Check invocations for DbSet<T>.Remove/RemoveRange/Update/ExecuteUpdate/ExecuteDelete
                        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
                        {
                            if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
                                continue;

                            var methodName = memberAccess.Name.Identifier.Text;

                            if (mutatingMethods.Contains(methodName))
                            {
                                // Check if receiver is DbSet<T> where T is protected
                                var receiverTypeInfo = semanticModel.GetTypeInfo(memberAccess.Expression);
                                if (IsProtectedDbSet(receiverTypeInfo.Type, protectedNames))
                                {
                                    var entityTypeName = GetDbSetEntityTypeName(receiverTypeInfo.Type);
                                    AddViolation(entries, invocation, tree, solutionDir,
                                        $"{methodName} on append-only type {entityTypeName}");
                                }
                            }
                            else if (bulkMutatingMethods.Contains(methodName))
                            {
                                // These can be called on IQueryable chains — check the root of the chain
                                var rootReceiver = GetChainRoot(memberAccess);
                                var rootTypeInfo = semanticModel.GetTypeInfo(rootReceiver);
                                if (IsProtectedDbSet(rootTypeInfo.Type, protectedNames))
                                {
                                    var entityTypeName = GetDbSetEntityTypeName(rootTypeInfo.Type);
                                    AddViolation(entries, invocation, tree, solutionDir,
                                        $"{methodName} on append-only type {entityTypeName}");
                                }
                            }
                        }

                        // 2. Check property assignments on protected types
                        foreach (var assignment in root.DescendantNodes().OfType<AssignmentExpressionSyntax>())
                        {
                            if (assignment.Left is not MemberAccessExpressionSyntax memberAccess)
                                continue;

                            var receiverSymbol = semanticModel.GetSymbolInfo(memberAccess.Expression).Symbol;
                            ITypeSymbol? receiverType = receiverSymbol switch
                            {
                                ILocalSymbol local => local.Type,
                                IParameterSymbol param => param.Type,
                                IFieldSymbol field => field.Type,
                                IPropertySymbol prop => prop.Type,
                                _ => null
                            };

                            if (receiverType is null || !IsProtectedType(receiverType, protectedNames))
                                continue;

                            // Allow assignments inside object initializers for Add/AddRange/AddAsync
                            if (IsInsideAddInitializer(assignment))
                                continue;

                            AddViolation(entries, assignment, tree, solutionDir,
                                $"property mutation on append-only type {receiverType.Name}");
                        }
                    }
                }

                // Dedup
                var deduped = entries
                    .GroupBy(e => $"{e.File}:{e.Line}:{e.Column}")
                    .Select(g => g.First())
                    .ToList();

                int? totalBeforeLimit = null;
                if (limit.HasValue && deduped.Count > limit.Value)
                {
                    totalBeforeLimit = deduped.Count;
                    deduped = deduped.Take(limit.Value).ToList();
                }

                OutputFormatter.WriteResults(
                    "audit-immutable",
                    typesRaw,
                    deduped,
                    format,
                    entry => entry,
                    totalBeforeLimit);

                sw.Stop();
                Telemetry.Log("audit-immutable", $"types={typesRaw}", totalBeforeLimit ?? deduped.Count, sw.ElapsedMilliseconds);
            }
        });

        return command;
    }

    private static bool IsProtectedType(ITypeSymbol type, HashSet<string> protectedNames)
    {
        return protectedNames.Contains(type.Name) || protectedNames.Contains(type.ToDisplayString());
    }

    private static bool IsProtectedDbSet(ITypeSymbol? type, HashSet<string> protectedNames)
    {
        if (type is null)
            return false;

        // Check DbSet<T> — the type itself or its ConstructedFrom
        if (type is INamedTypeSymbol named && named.Name == "DbSet" && named.TypeArguments.Length == 1)
        {
            return IsProtectedType(named.TypeArguments[0], protectedNames);
        }

        return false;
    }

    private static string GetDbSetEntityTypeName(ITypeSymbol? type)
    {
        if (type is INamedTypeSymbol named && named.Name == "DbSet" && named.TypeArguments.Length == 1)
            return named.TypeArguments[0].Name;
        return "?";
    }

    /// <summary>
    /// Walks a member access chain to find the root expression (e.g., _dbContext.AuditLogs from _dbContext.AuditLogs.Where(...).ExecuteDelete()).
    /// </summary>
    private static ExpressionSyntax GetChainRoot(MemberAccessExpressionSyntax memberAccess)
    {
        var current = memberAccess.Expression;
        while (current is InvocationExpressionSyntax inv && inv.Expression is MemberAccessExpressionSyntax inner)
        {
            current = inner.Expression;
        }
        return current;
    }

    private static bool IsInsideAddInitializer(SyntaxNode node)
    {
        var current = node.Parent;
        while (current is not null)
        {
            if (current is InitializerExpressionSyntax && current.Parent is ObjectCreationExpressionSyntax)
            {
                // Walk up to find if this object creation is an argument to Add/AddRange/AddAsync
                var creation = current.Parent;
                var maybeArgument = creation?.Parent;

                // Could be directly in an argument list
                if (maybeArgument is ArgumentSyntax arg && arg.Parent is ArgumentListSyntax argList
                    && argList.Parent is InvocationExpressionSyntax inv)
                {
                    var name = GetMethodNameFromInvocation(inv);
                    if (name is "Add" or "AddRange" or "AddAsync")
                        return true;
                }

                // Could be directly the argument to a single-arg method: _dbContext.AuditLogs.Add(new AuditLog { ... })
                if (maybeArgument is ArgumentListSyntax argList2
                    && argList2.Parent is InvocationExpressionSyntax inv2)
                {
                    var name = GetMethodNameFromInvocation(inv2);
                    if (name is "Add" or "AddRange" or "AddAsync")
                        return true;
                }
            }

            // Also check: the object creation itself is the expression passed to Add
            // e.g., _dbContext.AuditLogs.Add(new AuditLog { Action = action })
            if (current is ObjectCreationExpressionSyntax objCreation)
            {
                var parent = objCreation.Parent;
                if (parent is ArgumentSyntax argSyntax
                    && argSyntax.Parent is ArgumentListSyntax argListSyntax
                    && argListSyntax.Parent is InvocationExpressionSyntax invSyntax)
                {
                    var name = GetMethodNameFromInvocation(invSyntax);
                    if (name is "Add" or "AddRange" or "AddAsync")
                        return true;
                }
            }

            current = current.Parent;
        }
        return false;
    }

    private static string GetMethodNameFromInvocation(InvocationExpressionSyntax invocation)
    {
        return invocation.Expression switch
        {
            MemberAccessExpressionSyntax ma => ma.Name.Identifier.Text,
            IdentifierNameSyntax id => id.Identifier.Text,
            _ => ""
        };
    }

    private static void AddViolation(
        List<ResultEntry> entries,
        SyntaxNode node,
        SyntaxTree tree,
        string solutionDir,
        string description)
    {
        var location = node.GetLocation();
        var lineSpan = location.GetLineSpan();
        var filePath = LocationHelper.NormalizePath(lineSpan.Path, solutionDir);
        var line = lineSpan.StartLinePosition.Line + 1;
        var column = lineSpan.StartLinePosition.Character + 1;
        var text = tree.GetText();
        var sourceLine = text.Lines[lineSpan.StartLinePosition.Line].ToString().Trim();

        entries.Add(new ResultEntry(filePath, line, column, $"{sourceLine} -- {description}", ""));
    }
}
