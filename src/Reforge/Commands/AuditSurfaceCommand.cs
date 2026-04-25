using System.CommandLine;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;

namespace Reforge.Commands;

public static class AuditSurfaceCommand
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static Command Create(Option<string?> solutionOption, Option<OutputFormat> formatOption, Option<int?> limitOption)
    {
        var typeArg = new Argument<string>("type")
        {
            Description = "The interface or class to audit (per-method caller counts; class targets get body-shape classification)"
        };

        var command = new Command("audit-surface",
            "Per-method inbound view: caller counts split by prod/test, plus body shape for classes")
        {
            typeArg
        };

        command.SetAction(async (parseResult, ct) =>
        {
            var sw = Stopwatch.StartNew();
            var solutionPath = parseResult.GetValue(solutionOption);
            var format = parseResult.GetValue(formatOption);
            var symbolQuery = parseResult.GetValue(typeArg)!;
            var limit = parseResult.GetValue(limitOption);

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
                    OutputFormatter.WriteMessage("audit-surface", msg, format);
                    sw.Stop();
                    Telemetry.Log("audit-surface", symbolQuery, 0, sw.ElapsedMilliseconds);
                    return;
                }

                if (symbols.Count > 1)
                {
                    var types = symbols.OfType<INamedTypeSymbol>().ToList();
                    if (types.Count == 1)
                        symbols = [types[0]];
                    else
                    {
                        var candidates = string.Join(", ", symbols.Select(s => s.ToDisplayString()));
                        OutputFormatter.WriteMessage("audit-surface",
                            $"Ambiguous symbol '{symbolQuery}'. Candidates: {candidates}", format);
                        sw.Stop();
                        Telemetry.Log("audit-surface", $"{symbolQuery} (ambiguous, {symbols.Count} candidates)", 0, sw.ElapsedMilliseconds);
                        return;
                    }
                }

                if (symbols[0] is not INamedTypeSymbol typeSymbol)
                {
                    OutputFormatter.WriteMessage("audit-surface",
                        $"Symbol '{symbolQuery}' is not a type (it is a {symbols[0].Kind}).", format);
                    sw.Stop();
                    Telemetry.Log("audit-surface", symbolQuery, 0, sw.ElapsedMilliseconds);
                    return;
                }

                var solutionDir = LocationHelper.GetSolutionDirectory(solution);
                var isInterface = typeSymbol.TypeKind == TypeKind.Interface;
                var isClass = typeSymbol.TypeKind == TypeKind.Class;

                if (!isInterface && !isClass)
                {
                    OutputFormatter.WriteMessage("audit-surface",
                        $"audit-surface only supports interfaces and classes (got {typeSymbol.TypeKind}).", format);
                    sw.Stop();
                    Telemetry.Log("audit-surface", symbolQuery, 0, sw.ElapsedMilliseconds);
                    return;
                }

                // Find implementations for interface targets (used to display in the header
                // and to merge concrete-call sites into per-method caller counts).
                List<INamedTypeSymbol> implementations = [];
                if (isInterface)
                {
                    var impls = await SymbolFinder.FindImplementationsAsync(typeSymbol, solution, cancellationToken: ct);
                    implementations = impls
                        .OfType<INamedTypeSymbol>()
                        .Where(t => t.Locations.Any(l => l.IsInSource))
                        .GroupBy(t => t.ToDisplayString())
                        .Select(g => g.First())
                        .OrderBy(t => t.Name, StringComparer.Ordinal)
                        .ToList();
                }

                // Collect ordinary methods declared on this type (skip property accessors,
                // ctors, finalizers, operators, implicitly-declared members).
                var methods = typeSymbol.GetMembers()
                    .OfType<IMethodSymbol>()
                    .Where(m => !m.IsImplicitlyDeclared)
                    .Where(m => m.MethodKind == MethodKind.Ordinary)
                    .Where(m => m.AssociatedSymbol is null)
                    .Where(m => m.Locations.Any(l => l.IsInSource))
                    .ToList();

                // Cache compilations per project so per-method body classification can use the
                // semantic model to resolve receiver types accurately.
                var compilations = new Dictionary<ProjectId, Compilation?>();
                async Task<SemanticModel?> GetSemanticModelAsync(SyntaxTree tree)
                {
                    var project = solution.Projects.FirstOrDefault(p => p.Documents.Any(d => d.FilePath == tree.FilePath));
                    if (project is null) return null;
                    if (!compilations.TryGetValue(project.Id, out var comp))
                    {
                        comp = await project.GetCompilationAsync(ct);
                        compilations[project.Id] = comp;
                    }
                    return comp?.GetSemanticModel(tree);
                }

                // Issue #7: receiver-kind classification needs the class's instance fields/props.
                var instanceMembers = isClass
                    ? AuditDownstreamCommand.CollectInstanceMembers(typeSymbol)
                    : new Dictionary<string, ITypeSymbol>(StringComparer.Ordinal);

                var reports = new List<MethodSurfaceReport>();
                foreach (var method in methods)
                {
                    var callerLocations = await CollectCallerLocationsAsync(method, solution, ct);

                    var prod = new List<ResultEntry>();
                    var tests = new List<ResultEntry>();
                    foreach (var (loc, callingSymbol) in callerLocations)
                    {
                        var entry = LocationHelper.ToResultEntry(loc, callingSymbol, solutionDir);
                        if (IsTestPath(loc.GetLineSpan().Path))
                            tests.Add(entry);
                        else
                            prod.Add(entry);
                    }

                    string? bodyShape = null;
                    string? bodyHint = null;
                    if (isClass)
                    {
                        var declRef = method.DeclaringSyntaxReferences.FirstOrDefault();
                        var sem = declRef is null ? null : await GetSemanticModelAsync(declRef.SyntaxTree);
                        (bodyShape, bodyHint) = ClassifyBodyShape(method, sem, instanceMembers);
                    }

                    reports.Add(new MethodSurfaceReport(
                        method,
                        FormatSignature(method),
                        prod,
                        tests,
                        bodyShape,
                        bodyHint));
                }

                int total = reports.Count;
                int? totalBeforeLimit = null;
                if (limit.HasValue && reports.Count > limit.Value)
                {
                    totalBeforeLimit = reports.Count;
                    reports = reports.Take(limit.Value).ToList();
                }

                if (format == OutputFormat.Json)
                    WriteJson(typeSymbol, isInterface, implementations, reports, totalBeforeLimit);
                else
                    WriteCompact(typeSymbol, isInterface, implementations, reports, totalBeforeLimit);

                sw.Stop();
                Telemetry.Log("audit-surface", symbolQuery, totalBeforeLimit ?? total, sw.ElapsedMilliseconds);
            }
        });

        return command;
    }

    private static async Task<List<(Location Location, ISymbol CallingSymbol)>> CollectCallerLocationsAsync(
        IMethodSymbol method, Solution solution, CancellationToken ct)
    {
        var seen = new HashSet<string>();
        var results = new List<(Location, ISymbol)>();

        async Task AddCallersAsync(IMethodSymbol target)
        {
            var callers = await SymbolFinder.FindCallersAsync(target, solution, ct);
            foreach (var info in callers)
            {
                foreach (var loc in info.Locations)
                {
                    var ls = loc.GetLineSpan();
                    var key = $"{ls.Path}:{ls.StartLinePosition.Line}:{ls.StartLinePosition.Character}";
                    if (seen.Add(key))
                        results.Add((loc, info.CallingSymbol));
                }
            }
        }

        await AddCallersAsync(method);

        // For interface methods, also count direct calls on concrete implementations.
        if (method.ContainingType.TypeKind == TypeKind.Interface)
        {
            var impls = await SymbolFinder.FindImplementationsAsync(method, solution, cancellationToken: ct);
            foreach (var impl in impls.OfType<IMethodSymbol>())
                await AddCallersAsync(impl);
        }

        return results;
    }

    /// <summary>
    /// Heuristic body-shape classification. Order: write > passthrough > linq-over-source > composite > complex.
    /// Issue #7 splits passthrough/linq variants by receiver kind (repo / service / self).
    /// Returns (shape, optional hint for compact output).
    /// </summary>
    private static (string Shape, string? Hint) ClassifyBodyShape(
        IMethodSymbol method,
        SemanticModel? semanticModel,
        Dictionary<string, ITypeSymbol> instanceMembers)
    {
        var declRef = method.DeclaringSyntaxReferences.FirstOrDefault();
        if (declRef is null)
            return ("complex", null);

        var node = declRef.GetSyntax();
        if (node is not MethodDeclarationSyntax methodDecl)
            return ("complex", null);

        if (methodDecl.Body is null && methodDecl.ExpressionBody is null)
            return ("abstract", null); // shouldn't happen for ordinary class methods, but be safe

        var allInvocations = methodDecl.DescendantNodes().OfType<InvocationExpressionSyntax>().ToList();

        // 1. Write — body contains a mutation API on a DbSet/DbContext.
        foreach (var invoke in allInvocations)
        {
            if (invoke.Expression is MemberAccessExpressionSyntax ma)
            {
                var name = ma.Name.Identifier.Text;
                if (!IsWriteApiName(name))
                    continue;

                // SaveChanges* and ExecuteUpdate*/ExecuteDelete* are EF-specific — always count.
                if (IsAlwaysWriteApi(name))
                    return ("write", name);

                // Add/Update/Remove only count as a DbSet write if the receiver is a DbSet/DbContext
                // (or, lacking a semantic model, falls back to receiver-name heuristics).
                if (IsLikelyDbContextWrite(ma, semanticModel))
                    return ("write", name);
            }
        }

        // Identify a single body expression for passthrough / linq detection.
        ExpressionSyntax? singleExpr = null;
        if (methodDecl.ExpressionBody is not null)
        {
            singleExpr = methodDecl.ExpressionBody.Expression;
        }
        else if (methodDecl.Body is { Statements.Count: 1 } block)
        {
            singleExpr = block.Statements[0] switch
            {
                ReturnStatementSyntax ret => ret.Expression,
                ExpressionStatementSyntax es => es.Expression,
                _ => null
            };
        }

        if (singleExpr is not null)
        {
            var inner = StripAwaitAndParens(singleExpr);

            // 2. Passthrough — body is a single non-LINQ method invocation.
            if (inner is InvocationExpressionSyntax pinv)
            {
                switch (pinv.Expression)
                {
                    case MemberAccessExpressionSyntax pma when !IsLinqMethodName(pma.Name.Identifier.Text):
                    {
                        var hint = $"{DescribeReceiver(pma)}.{pma.Name.Identifier.Text}";
                        var kind = ClassifyReceiverKind(pma.Expression, instanceMembers);
                        var shape = kind is null ? "passthrough" : $"passthrough-{kind}";
                        return (shape, hint);
                    }
                    case IdentifierNameSyntax pid when !IsLinqMethodName(pid.Identifier.Text):
                    {
                        // Bare invocation -> method on `this` (private/inherited helper).
                        return ("passthrough-self", $"this.{pid.Identifier.Text}");
                    }
                }
            }

            // 3. LINQ chain over a single source.
            if (inner is InvocationExpressionSyntax linv && IsLinqChain(linv, out var source, out var sourceExpr))
            {
                var hint = source is null ? null : $"LINQ chain over {source}";
                var kind = sourceExpr is null ? null : ClassifyReceiverKind(sourceExpr, instanceMembers);
                var shape = kind is null || kind == "self" ? "linq-over-source" : $"linq-over-{kind}";
                return (shape, hint);
            }
        }

        // 4. Composite — 3+ method calls or multi-field projection
        if (allInvocations.Count >= 3)
            return ("composite", $"{allInvocations.Count} calls");

        var multiFieldInit = methodDecl.DescendantNodes()
            .OfType<InitializerExpressionSyntax>()
            .Any(init => init.Expressions.Count >= 3);

        var anonProj = methodDecl.DescendantNodes()
            .OfType<AnonymousObjectCreationExpressionSyntax>()
            .Any(a => a.Initializers.Count >= 3);

        if (multiFieldInit || anonProj)
            return ("composite", "multi-field projection");

        return ("complex", null);
    }

    private static bool IsWriteApiName(string name) =>
        name is "SaveChangesAsync" or "SaveChanges"
                or "Add" or "AddAsync" or "AddRange" or "AddRangeAsync"
                or "Update" or "UpdateRange"
                or "Remove" or "RemoveRange"
                or "ExecuteDelete" or "ExecuteDeleteAsync"
                or "ExecuteUpdate" or "ExecuteUpdateAsync";

    private static bool IsAlwaysWriteApi(string name) =>
        name is "SaveChangesAsync" or "SaveChanges"
                or "ExecuteDelete" or "ExecuteDeleteAsync"
                or "ExecuteUpdate" or "ExecuteUpdateAsync";

    /// <summary>
    /// Returns true when an Add/Update/Remove call is likely targeting a DbSet/DbContext.
    /// Uses the semantic model when available; otherwise falls back to a name heuristic
    /// to avoid flagging every cache.Remove/list.Add call as a DB write.
    /// </summary>
    private static bool IsLikelyDbContextWrite(MemberAccessExpressionSyntax ma, SemanticModel? semanticModel)
    {
        if (semanticModel is not null)
        {
            var receiverType = semanticModel.GetTypeInfo(ma.Expression).Type;
            if (receiverType is null)
                return false;
            return DbContextAnalyzer.IsDbContextType(receiverType) || DbContextAnalyzer.IsDbSetType(receiverType);
        }

        // Fallback: receiver chain bottoms out at an identifier whose name suggests a DbContext.
        var current = ma.Expression;
        while (true)
        {
            switch (current)
            {
                case InvocationExpressionSyntax inv:
                    current = inv.Expression;
                    break;
                case MemberAccessExpressionSyntax inner:
                    current = inner.Expression;
                    break;
                case IdentifierNameSyntax id:
                    var n = id.Identifier.Text;
                    return n.Contains("Context", StringComparison.OrdinalIgnoreCase)
                        || n.Contains("_db", StringComparison.OrdinalIgnoreCase);
                default:
                    return false;
            }
        }
    }

    private static bool IsLinqMethodName(string name) =>
        name is "Where" or "Select" or "SelectMany" or "Any" or "All"
                or "First" or "FirstOrDefault" or "Single" or "SingleOrDefault"
                or "Last" or "LastOrDefault" or "Count" or "LongCount"
                or "ToList" or "ToArray" or "ToDictionary" or "ToHashSet"
                or "OrderBy" or "OrderByDescending" or "ThenBy" or "ThenByDescending"
                or "GroupBy" or "Distinct" or "Skip" or "Take"
                or "ToListAsync" or "ToArrayAsync" or "ToDictionaryAsync"
                or "FirstOrDefaultAsync" or "FirstAsync"
                or "SingleOrDefaultAsync" or "SingleAsync"
                or "AnyAsync" or "AllAsync" or "CountAsync" or "LongCountAsync"
                or "Sum" or "Min" or "Max" or "Average"
                or "Include" or "ThenInclude" or "AsNoTracking" or "AsTracking"
                or "Contains";

    /// <summary>
    /// True when the invocation is a chain of LINQ methods rooted on a single source.
    /// Sets <paramref name="source"/> to a short description of that source and
    /// <paramref name="sourceExpr"/> to the raw source expression so the caller can
    /// classify its receiver kind (issue #7).
    /// </summary>
    private static bool IsLinqChain(InvocationExpressionSyntax invocation, out string? source, out ExpressionSyntax? sourceExpr)
    {
        source = null;
        sourceExpr = null;
        var current = (ExpressionSyntax)invocation;
        var linqCalls = 0;

        while (current is InvocationExpressionSyntax inv && inv.Expression is MemberAccessExpressionSyntax ma)
        {
            if (!IsLinqMethodName(ma.Name.Identifier.Text))
                return false;
            linqCalls++;
            current = ma.Expression;
        }

        if (linqCalls < 1)
            return false;

        sourceExpr = current;
        source = current switch
        {
            IdentifierNameSyntax id => id.Identifier.Text,
            MemberAccessExpressionSyntax ma => DescribeMemberAccess(ma),
            InvocationExpressionSyntax => "method result",
            _ => null
        };
        return true;
    }

    /// <summary>
    /// Strip enclosing parentheses and leading awaits from an expression. Used to
    /// peel patterns like `(await _service.X())` down to the underlying invocation.
    /// </summary>
    private static ExpressionSyntax StripAwaitAndParens(ExpressionSyntax expr)
    {
        while (true)
        {
            switch (expr)
            {
                case AwaitExpressionSyntax aw: expr = aw.Expression; break;
                case ParenthesizedExpressionSyntax pe: expr = pe.Expression; break;
                default: return expr;
            }
        }
    }

    /// <summary>
    /// Classify the receiver of a passthrough or LINQ source as "repo", "service", "self",
    /// or null when it can't be tied to a known instance field/property of the class.
    /// Issue #7 — distinguishes architectural-boundary passthroughs (repo) from
    /// caller-LINQ-disguise passthroughs (service).
    /// </summary>
    private static string? ClassifyReceiverKind(
        ExpressionSyntax receiver,
        Dictionary<string, ITypeSymbol> instanceMembers)
    {
        receiver = StripAwaitAndParens(receiver);

        if (receiver is ThisExpressionSyntax)
            return "self";

        if (receiver is MemberAccessExpressionSyntax thisMa && thisMa.Expression is ThisExpressionSyntax)
        {
            return instanceMembers.TryGetValue(thisMa.Name.Identifier.Text, out var t)
                ? ClassifyDependencyKind(t)
                : "self";
        }

        if (receiver is IdentifierNameSyntax id)
        {
            return instanceMembers.TryGetValue(id.Identifier.Text, out var t)
                ? ClassifyDependencyKind(t)
                : null;
        }

        var root = FindRootIdentifier(receiver);
        if (root is null)
            return null;

        if (instanceMembers.TryGetValue(root.Identifier.Text, out var rootType))
            return ClassifyDependencyKind(rootType);
        return null;
    }

    private static IdentifierNameSyntax? FindRootIdentifier(ExpressionSyntax expr)
    {
        while (true)
        {
            switch (expr)
            {
                case AwaitExpressionSyntax aw: expr = aw.Expression; break;
                case ParenthesizedExpressionSyntax pe: expr = pe.Expression; break;
                case InvocationExpressionSyntax inv: expr = inv.Expression; break;
                case MemberAccessExpressionSyntax ma:
                    if (ma.Expression is ThisExpressionSyntax)
                        return ma.Name as IdentifierNameSyntax;
                    expr = ma.Expression;
                    break;
                case ConditionalAccessExpressionSyntax ca: expr = ca.Expression; break;
                case IdentifierNameSyntax id: return id;
                default: return null;
            }
        }
    }

    /// <summary>
    /// Issue #7 receiver-type taxonomy: "repo" covers DbContext, IDbContextFactory&lt;T&gt;,
    /// and any type whose name ends with "Repository" (matches I*Repository and *Repository);
    /// everything else is treated as "service".
    /// </summary>
    private static string ClassifyDependencyKind(ITypeSymbol type)
    {
        if (DbContextAnalyzer.IsDbContextType(type))
            return "repo";
        if (type is INamedTypeSymbol named && named.ConstructedFrom?.Name == "IDbContextFactory")
            return "repo";
        if (type.Name.EndsWith("Repository", StringComparison.Ordinal))
            return "repo";
        return "service";
    }

    private static string DescribeReceiver(MemberAccessExpressionSyntax access)
    {
        return access.Expression switch
        {
            IdentifierNameSyntax id => id.Identifier.Text,
            MemberAccessExpressionSyntax inner => DescribeMemberAccess(inner),
            ThisExpressionSyntax => "this",
            _ => access.Expression.ToString()
        };
    }

    private static string DescribeMemberAccess(MemberAccessExpressionSyntax ma)
    {
        var receiver = ma.Expression switch
        {
            IdentifierNameSyntax id => id.Identifier.Text,
            ThisExpressionSyntax => "this",
            MemberAccessExpressionSyntax inner => DescribeMemberAccess(inner),
            _ => ma.Expression.ToString()
        };
        return $"{receiver}.{ma.Name.Identifier.Text}";
    }

    private static bool IsTestPath(string filePath)
    {
        if (string.IsNullOrEmpty(filePath))
            return false;
        var p = filePath.Replace('\\', '/');
        // Spec patterns: */tests/*, *.Tests.csproj, *Tests.cs.
        // The middle pattern manifests as a file path containing ".Tests/" since each project
        // sits in a directory matching the .csproj name.
        if (p.Contains("/tests/", StringComparison.OrdinalIgnoreCase))
            return true;
        if (p.Contains(".Tests/", StringComparison.Ordinal))
            return true;
        if (p.EndsWith("Tests.cs", StringComparison.Ordinal))
            return true;
        return false;
    }

    private static string FormatSignature(IMethodSymbol method)
    {
        var format = new SymbolDisplayFormat(
            memberOptions: SymbolDisplayMemberOptions.IncludeParameters | SymbolDisplayMemberOptions.IncludeType,
            parameterOptions: SymbolDisplayParameterOptions.IncludeType,
            genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
            miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes
                | SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers);

        var minimal = method.ToDisplayString(format);
        // ToDisplayString with the above produces: ReturnType MethodName(ParamTypes)
        // Convert to: MethodName(ParamTypes) -> ReturnType (matches issue example)
        if (method.ReturnsVoid)
            return $"{method.Name}({string.Join(", ", method.Parameters.Select(p => p.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)))}) -> void";

        var paramList = string.Join(", ", method.Parameters.Select(p => p.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)));
        var ret = method.ReturnType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
        return $"{method.Name}({paramList}) -> {ret}";
    }

    private static void WriteCompact(
        INamedTypeSymbol typeSymbol,
        bool isInterface,
        List<INamedTypeSymbol> implementations,
        List<MethodSurfaceReport> reports,
        int? totalBeforeLimit)
    {
        var header = $"{typeSymbol.ToDisplayString()} - {reports.Count}";
        if (totalBeforeLimit.HasValue)
            header += $" of {totalBeforeLimit.Value}";
        header += reports.Count == 1 ? " method" : " methods";

        if (isInterface && implementations.Count > 0)
        {
            var implNames = string.Join(", ", implementations.Select(t => t.Name));
            header += $", {implementations.Count} implementation{(implementations.Count == 1 ? "" : "s")} ({implNames})";
        }

        Console.WriteLine(header);

        foreach (var r in reports)
        {
            Console.WriteLine();
            Console.WriteLine(r.Signature);
            Console.WriteLine($"  Callers: {r.ProdCallers.Count} prod, {r.TestCallers.Count} test");

            foreach (var entry in r.ProdCallers)
                Console.WriteLine($"    {entry.File}:{entry.Line}");
            foreach (var entry in r.TestCallers)
                Console.WriteLine($"    {entry.File}:{entry.Line}");

            if (r.BodyShape is not null)
            {
                var line = $"  Body: {r.BodyShape}";
                if (!string.IsNullOrEmpty(r.BodyHint))
                    line += $" ({r.BodyHint})";
                Console.WriteLine(line);
            }
        }
    }

    private static void WriteJson(
        INamedTypeSymbol typeSymbol,
        bool isInterface,
        List<INamedTypeSymbol> implementations,
        List<MethodSurfaceReport> reports,
        int? totalBeforeLimit)
    {
        var payload = new
        {
            command = "audit-surface",
            symbol = typeSymbol.ToDisplayString(),
            kind = isInterface ? "interface" : "class",
            implementations = isInterface
                ? implementations.Select(t => t.ToDisplayString()).ToArray()
                : null,
            methods = reports.Select(r => new
            {
                name = r.Method.Name,
                signature = r.Signature,
                callers = new
                {
                    prodCount = r.ProdCallers.Count,
                    testCount = r.TestCallers.Count,
                    prod = r.ProdCallers.Select(e => new { file = e.File, line = e.Line, column = e.Column, context = e.Context }).ToArray(),
                    test = r.TestCallers.Select(e => new { file = e.File, line = e.Line, column = e.Column, context = e.Context }).ToArray()
                },
                bodyShape = r.BodyShape,
                bodyHint = r.BodyHint
            }).ToArray(),
            total = reports.Count,
            totalBeforeLimit
        };

        Console.WriteLine(JsonSerializer.Serialize(payload, JsonOptions));
    }

    private record MethodSurfaceReport(
        IMethodSymbol Method,
        string Signature,
        List<ResultEntry> ProdCallers,
        List<ResultEntry> TestCallers,
        string? BodyShape,
        string? BodyHint);
}
