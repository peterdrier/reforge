using System.CommandLine;
using System.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Reforge.Commands;

public static class AuditCacheCommand
{
    private static readonly HashSet<string> DefaultCacheMethodNames = new(StringComparer.Ordinal)
    {
        "Remove", "RemoveAsync", "Set", "SetAsync", "CreateEntry", "Evict", "EvictAsync"
    };

    public static Command Create(Option<string?> solutionOption, Option<OutputFormat> formatOption, Option<int?> limitOption)
    {
        var cacheMethodOption = new Option<string[]>("--cache-method")
        {
            Description = "Additional cache method names to recognize (e.g., InvalidateCache)",
            AllowMultipleArgumentsPerToken = true
        };

        var command = new Command("audit-cache", "Find SaveChangesAsync calls without corresponding cache eviction")
        {
            cacheMethodOption
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var solutionPath = parseResult.GetValue(solutionOption);
            var format = parseResult.GetValue(formatOption);
            var limit = parseResult.GetValue(limitOption);
            var extraCacheMethods = parseResult.GetValue(cacheMethodOption) ?? [];
            var sw = Stopwatch.StartNew();

            var cacheMethodNames = new HashSet<string>(DefaultCacheMethodNames, StringComparer.Ordinal);
            foreach (var m in extraCacheMethods)
                cacheMethodNames.Add(m);

            var (solution, handle) = await WorkspaceHelper.OpenSolutionAsync(solutionPath);
            using (handle)
            {
                var solutionDir = LocationHelper.GetSolutionDirectory(solution);
                var results = new List<ResultEntry>();

                foreach (var project in solution.Projects)
                {
                    if (project.Name.Contains("Test", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var compilation = await project.GetCompilationAsync(cancellationToken);
                    if (compilation is null)
                        continue;

                    foreach (var document in project.Documents)
                    {
                        var root = await document.GetSyntaxRootAsync(cancellationToken);
                        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
                        if (root is null || semanticModel is null)
                            continue;

                        var classes = root.DescendantNodes().OfType<ClassDeclarationSyntax>();

                        foreach (var classDecl in classes)
                        {
                            var classSymbol = semanticModel.GetDeclaredSymbol(classDecl, cancellationToken) as INamedTypeSymbol;
                            if (classSymbol is null)
                                continue;

                            // Find constructor fields/params
                            var (hasDbContext, cacheFieldNames) = AnalyzeClassDependencies(classSymbol);

                            if (!hasDbContext || cacheFieldNames.Count == 0)
                                continue;

                            // Build a set of method names in this class that touch the cache
                            var methodsThatTouchCache = new HashSet<string>(StringComparer.Ordinal);
                            foreach (var method in classDecl.Members.OfType<MethodDeclarationSyntax>())
                            {
                                if (MethodTouchesCache(method, cacheFieldNames, cacheMethodNames))
                                {
                                    var name = method.Identifier.Text;
                                    methodsThatTouchCache.Add(name);
                                }
                            }

                            // Now find methods that call SaveChanges but don't touch cache
                            foreach (var method in classDecl.Members.OfType<MethodDeclarationSyntax>())
                            {
                                if (!MethodCallsSaveChanges(method))
                                    continue;

                                bool touchesCache = MethodTouchesCache(method, cacheFieldNames, cacheMethodNames);

                                // One-level-deep: check if any same-class method call touches cache
                                if (!touchesCache)
                                {
                                    touchesCache = CallsSameClassMethodThatTouchesCache(
                                        method, semanticModel, classSymbol, methodsThatTouchCache, cancellationToken);
                                }

                                if (!touchesCache)
                                {
                                    var lineSpan = method.Identifier.GetLocation().GetLineSpan();
                                    var filePath = LocationHelper.NormalizePath(lineSpan.Path, solutionDir);
                                    var line = lineSpan.StartLinePosition.Line + 1;
                                    var column = lineSpan.StartLinePosition.Character + 1;
                                    var context = $"{method.Identifier.Text} — SaveChangesAsync without cache eviction";
                                    results.Add(new ResultEntry(filePath, line, column, context, classSymbol.Name));
                                }
                            }
                        }
                    }
                }

                int? totalBeforeLimit = null;
                if (limit.HasValue && results.Count > limit.Value)
                {
                    totalBeforeLimit = results.Count;
                    results = results.Take(limit.Value).ToList();
                }

                OutputFormatter.WriteResults("audit-cache", "issues", results, format, r => r, totalBeforeLimit);

                sw.Stop();
                Telemetry.Log("audit-cache", "", totalBeforeLimit ?? results.Count, sw.ElapsedMilliseconds);
            }
        });

        return command;
    }

    /// <summary>
    /// Analyzes a class's constructor parameters and fields to determine if it has both
    /// a DbContext dependency and a cache dependency. Returns the cache field names.
    /// </summary>
    private static (bool hasDbContext, HashSet<string> cacheFieldNames) AnalyzeClassDependencies(INamedTypeSymbol classSymbol)
    {
        bool hasDbContext = false;
        var cacheFieldNames = new HashSet<string>(StringComparer.Ordinal);

        foreach (var member in classSymbol.GetMembers())
        {
            ITypeSymbol? fieldType = null;
            string? fieldName = null;

            if (member is IFieldSymbol field)
            {
                fieldType = field.Type;
                fieldName = field.Name;
            }
            else if (member is IPropertySymbol prop)
            {
                fieldType = prop.Type;
                fieldName = prop.Name;
            }

            if (fieldType is null || fieldName is null)
                continue;

            if (IsDbContextType(fieldType))
                hasDbContext = true;

            if (IsCacheType(fieldType))
                cacheFieldNames.Add(fieldName);
        }

        // Also check constructor parameters (in case they aren't stored as fields with known names)
        foreach (var ctor in classSymbol.Constructors)
        {
            foreach (var param in ctor.Parameters)
            {
                if (IsDbContextType(param.Type))
                    hasDbContext = true;
                // Constructor params with cache types might map to fields we already found
            }
        }

        return (hasDbContext, cacheFieldNames);
    }

    private static bool IsDbContextType(ITypeSymbol type)
    {
        var current = type;
        while (current != null)
        {
            if (current.Name == "DbContext" || current.Name.EndsWith("DbContext"))
                return true;
            current = current.BaseType;
        }
        return false;
    }

    private static bool IsCacheType(ITypeSymbol type)
    {
        var name = type.Name;
        if (name is "IMemoryCache" or "IDistributedCache")
            return true;
        if (name.Contains("Cache"))
            return true;
        // Check interfaces
        foreach (var iface in type.AllInterfaces)
        {
            if (iface.Name is "IMemoryCache" or "IDistributedCache" || iface.Name.Contains("Cache"))
                return true;
        }
        return false;
    }

    private static bool MethodCallsSaveChanges(MethodDeclarationSyntax method)
    {
        if (method.Body is null && method.ExpressionBody is null)
            return false;

        var invocations = method.DescendantNodes().OfType<InvocationExpressionSyntax>();
        foreach (var invocation in invocations)
        {
            var methodName = GetInvokedMethodName(invocation);
            if (methodName is "SaveChangesAsync" or "SaveChanges")
                return true;
        }
        return false;
    }

    private static bool MethodTouchesCache(
        MethodDeclarationSyntax method,
        HashSet<string> cacheFieldNames,
        HashSet<string> cacheMethodNames)
    {
        if (method.Body is null && method.ExpressionBody is null)
            return false;

        var invocations = method.DescendantNodes().OfType<InvocationExpressionSyntax>();
        foreach (var invocation in invocations)
        {
            if (invocation.Expression is MemberAccessExpressionSyntax memberAccess)
            {
                var receiverName = GetReceiverName(memberAccess.Expression);
                var invokedName = memberAccess.Name.Identifier.Text;

                if (receiverName != null && cacheFieldNames.Contains(receiverName) && cacheMethodNames.Contains(invokedName))
                    return true;
            }
        }
        return false;
    }

    private static bool CallsSameClassMethodThatTouchesCache(
        MethodDeclarationSyntax method,
        SemanticModel semanticModel,
        INamedTypeSymbol classSymbol,
        HashSet<string> methodsThatTouchCache,
        CancellationToken cancellationToken)
    {
        var invocations = method.DescendantNodes().OfType<InvocationExpressionSyntax>();
        foreach (var invocation in invocations)
        {
            var symbolInfo = semanticModel.GetSymbolInfo(invocation, cancellationToken);
            if (symbolInfo.Symbol is IMethodSymbol calledMethod)
            {
                if (SymbolEqualityComparer.Default.Equals(calledMethod.ContainingType, classSymbol))
                {
                    if (methodsThatTouchCache.Contains(calledMethod.Name))
                        return true;
                }
            }

            // Also handle simple name invocations (e.g., calling EvictUserCache(id))
            var invokedName = GetInvokedMethodName(invocation);
            if (invokedName != null && methodsThatTouchCache.Contains(invokedName))
                return true;
        }
        return false;
    }

    private static string? GetInvokedMethodName(InvocationExpressionSyntax invocation)
    {
        return invocation.Expression switch
        {
            MemberAccessExpressionSyntax memberAccess => memberAccess.Name.Identifier.Text,
            IdentifierNameSyntax identifier => identifier.Identifier.Text,
            _ => null
        };
    }

    private static string? GetReceiverName(ExpressionSyntax expression)
    {
        return expression switch
        {
            IdentifierNameSyntax identifier => identifier.Identifier.Text,
            MemberAccessExpressionSyntax memberAccess => memberAccess.Name.Identifier.Text,
            _ => null
        };
    }
}
