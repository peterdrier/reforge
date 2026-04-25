using System.CommandLine;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;

namespace Reforge.Commands;

public static class AuditDownstreamCommand
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly HashSet<string> WriteApiNames = new(StringComparer.Ordinal)
    {
        "Add", "AddAsync", "AddRange", "AddRangeAsync",
        "Update", "UpdateRange",
        "Remove", "RemoveRange",
        "ExecuteDelete", "ExecuteDeleteAsync",
        "ExecuteUpdate", "ExecuteUpdateAsync"
    };

    public static Command Create(Option<string?> solutionOption, Option<OutputFormat> formatOption, Option<int?> limitOption)
    {
        var typeArg = new Argument<string>("class")
        {
            Description = "The class to audit (per-method outbound calls, DbSet accesses, and external IO)"
        };

        var command = new Command("audit-downstream",
            "Per-method outbound view: dependency calls, DbSet read/write (traced through repos), and external IO")
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
                    OutputFormatter.WriteMessage("audit-downstream", msg, format);
                    sw.Stop();
                    Telemetry.Log("audit-downstream", symbolQuery, 0, sw.ElapsedMilliseconds);
                    return;
                }

                if (symbols.Count > 1)
                {
                    var types = symbols.OfType<INamedTypeSymbol>().Where(t => t.TypeKind == TypeKind.Class).ToList();
                    if (types.Count == 1)
                        symbols = [types[0]];
                    else
                    {
                        var candidates = string.Join(", ", symbols.Select(s => s.ToDisplayString()));
                        OutputFormatter.WriteMessage("audit-downstream",
                            $"Ambiguous symbol '{symbolQuery}'. Candidates: {candidates}", format);
                        sw.Stop();
                        Telemetry.Log("audit-downstream", $"{symbolQuery} (ambiguous, {symbols.Count} candidates)", 0, sw.ElapsedMilliseconds);
                        return;
                    }
                }

                if (symbols[0] is not INamedTypeSymbol typeSymbol || typeSymbol.TypeKind != TypeKind.Class)
                {
                    OutputFormatter.WriteMessage("audit-downstream",
                        $"audit-downstream only supports classes (got '{symbols[0].ToDisplayString()}', kind {symbols[0].Kind}).", format);
                    sw.Stop();
                    Telemetry.Log("audit-downstream", symbolQuery, 0, sw.ElapsedMilliseconds);
                    return;
                }

                var instanceMembers = CollectInstanceMembers(typeSymbol);

                var methods = typeSymbol.GetMembers()
                    .OfType<IMethodSymbol>()
                    .Where(m => !m.IsImplicitlyDeclared)
                    .Where(m => m.MethodKind == MethodKind.Ordinary)
                    .Where(m => m.AssociatedSymbol is null)
                    .Where(m => m.Locations.Any(l => l.IsInSource))
                    .ToList();

                var helperTraceCache = new Dictionary<IMethodSymbol, MethodTrace>(SymbolEqualityComparer.Default);
                var repoBodyCache = new Dictionary<IMethodSymbol, RepoBodyResult>(SymbolEqualityComparer.Default);
                var reports = new List<MethodDownstreamReport>();
                foreach (var method in methods)
                {
                    var trace = await GetMethodTraceAsync(method, solution, instanceMembers, helperTraceCache, repoBodyCache, ct);

                    // A DbSet that's been written to in any branch shouldn't also be listed as a read.
                    var writeTables = trace.DbSetWrites.Select(w => w.Table).ToHashSet(StringComparer.Ordinal);
                    var reads = trace.DbSetReads
                        .Where(r => !writeTables.Contains(r.Table))
                        .OrderBy(r => r.Table, StringComparer.Ordinal)
                        .ThenBy(r => r.Via ?? "", StringComparer.Ordinal)
                        .ToList();
                    var writes = trace.DbSetWrites
                        .OrderBy(w => w.Table, StringComparer.Ordinal)
                        .ThenBy(w => w.Via ?? "", StringComparer.Ordinal)
                        .ToList();

                    reports.Add(new MethodDownstreamReport(
                        method,
                        trace.Calls.OrderBy(s => s, StringComparer.Ordinal).ToList(),
                        reads,
                        writes,
                        trace.External.OrderBy(s => s, StringComparer.Ordinal).ToList(),
                        trace.UntracedRepoCalls.OrderBy(s => s, StringComparer.Ordinal).ToList()));
                }

                int total = reports.Count;
                int? totalBeforeLimit = null;
                if (limit.HasValue && reports.Count > limit.Value)
                {
                    totalBeforeLimit = reports.Count;
                    reports = reports.Take(limit.Value).ToList();
                }

                if (format == OutputFormat.Json)
                    WriteJson(typeSymbol, reports, totalBeforeLimit);
                else
                    WriteCompact(typeSymbol, reports, totalBeforeLimit);

                sw.Stop();
                Telemetry.Log("audit-downstream", symbolQuery, totalBeforeLimit ?? total, sw.ElapsedMilliseconds);
            }
        });

        return command;
    }

    /// <summary>
    /// Map of instance field/property name -> declared type. Excludes statics.
    /// Shared with AuditSurfaceCommand for receiver-kind classification (issue #7).
    /// </summary>
    internal static Dictionary<string, ITypeSymbol> CollectInstanceMembers(INamedTypeSymbol type)
    {
        var result = new Dictionary<string, ITypeSymbol>(StringComparer.Ordinal);
        foreach (var member in type.GetMembers())
        {
            if (member.IsStatic)
                continue;
            switch (member)
            {
                case IFieldSymbol f when !f.IsImplicitlyDeclared:
                    result[f.Name] = f.Type;
                    break;
                case IPropertySymbol p when !p.IsImplicitlyDeclared:
                    result[p.Name] = p.Type;
                    break;
            }
        }
        return result;
    }

    /// <summary>
    /// Walks a method body classifying calls/DbSet accesses/external IO. For dependency
    /// calls into repository implementations, performs a one-hop body scan to surface
    /// DbSet usage with `via` attribution (issue #8). Private helpers are inlined via
    /// recursive trace; the recursion is bounded by a per-method cache.
    /// </summary>
    private static async Task<MethodTrace> GetMethodTraceAsync(
        IMethodSymbol method,
        Solution solution,
        Dictionary<string, ITypeSymbol> instanceMembers,
        Dictionary<IMethodSymbol, MethodTrace> helperCache,
        Dictionary<IMethodSymbol, RepoBodyResult> repoBodyCache,
        CancellationToken ct)
    {
        if (helperCache.TryGetValue(method, out var cached))
            return cached;

        var trace = new MethodTrace();
        helperCache[method] = trace;

        var (methodDecl, semanticModel) = await GetSyntaxAndModelAsync(method, solution, ct);
        if (methodDecl is null || semanticModel is null)
            return trace;
        if (methodDecl.Body is null && methodDecl.ExpressionBody is null)
            return trace;

        var bodyNodes = methodDecl.DescendantNodes().ToList();
        var saveChangesPresent = HasSaveChanges(bodyNodes);

        // 1. DbSet accesses directly in this method's body — via=null (direct).
        foreach (var (table, isWrite) in CollectDbSetAccesses(bodyNodes, semanticModel, saveChangesPresent))
        {
            if (isWrite) trace.DbSetWrites.Add((table, null));
            else trace.DbSetReads.Add((table, null));
        }

        // 2. Invocations.
        foreach (var invocation in bodyNodes.OfType<InvocationExpressionSyntax>())
        {
            var classification = ClassifyInvocation(invocation, semanticModel, method.ContainingType, instanceMembers);

            switch (classification.Kind)
            {
                case InvocationKind.DependencyCall:
                    trace.Calls.Add(classification.Display);

                    // One-hop trace into repository implementations only (issue #8).
                    // Service-to-service hops are surfaced via `calls`; the audit consumer
                    // can choose to drill down via a separate audit-downstream call.
                    if (classification.IsRepoReceiver
                        && classification.Target is { } repoTarget
                        && IsTraceableInSolution(repoTarget))
                    {
                        var repoBody = await AnalyzeRepoBodyAsync(repoTarget, solution, repoBodyCache, ct);
                        foreach (var t in repoBody.Reads)
                            trace.DbSetReads.Add((t, classification.Display));
                        foreach (var t in repoBody.Writes)
                            trace.DbSetWrites.Add((t, classification.Display));
                        foreach (var u in repoBody.UntracedRepoCalls)
                            trace.UntracedRepoCalls.Add(u);
                    }
                    break;

                case InvocationKind.PrivateHelper:
                    trace.Calls.Add(classification.Display);

                    if (classification.Target is { } helperTarget && IsTraceableInSolution(helperTarget))
                    {
                        var sub = await GetMethodTraceAsync(helperTarget, solution, instanceMembers, helperCache, repoBodyCache, ct);
                        foreach (var r in sub.DbSetReads) trace.DbSetReads.Add(r);
                        foreach (var w in sub.DbSetWrites) trace.DbSetWrites.Add(w);
                        foreach (var c in sub.Calls) trace.Calls.Add(c);
                        foreach (var x in sub.External) trace.External.Add(x);
                        foreach (var u in sub.UntracedRepoCalls) trace.UntracedRepoCalls.Add(u);
                    }
                    break;

                case InvocationKind.External:
                    trace.External.Add(classification.Display);
                    break;

                case InvocationKind.Ignored:
                    break;
            }
        }

        return trace;
    }

    /// <summary>
    /// One-hop scan of a repository method's body. Collects direct DbSet reads/writes
    /// and any invocations on other repository fields (which surface as untracedRepoCalls
    /// to the caller). Does NOT recurse into those repo-to-repo calls — the issue #8
    /// design caps tracing at one hop because repos are intentionally thin.
    ///
    /// When the call site targets an interface method, fans out to all implementations
    /// in the solution and merges their results — this is the typical case (services
    /// inject <c>IFooRepository</c>, not <c>FooRepository</c>).
    /// </summary>
    private static async Task<RepoBodyResult> AnalyzeRepoBodyAsync(
        IMethodSymbol method,
        Solution solution,
        Dictionary<IMethodSymbol, RepoBodyResult> cache,
        CancellationToken ct)
    {
        if (cache.TryGetValue(method, out var cached))
            return cached;

        var result = new RepoBodyResult();
        cache[method] = result;

        var concreteMethods = await ResolveConcreteMethodsAsync(method, solution, ct);
        foreach (var concrete in concreteMethods)
            await ScanRepoBodyIntoAsync(concrete, solution, result, ct);

        // A DbSet written in any branch shouldn't also be listed as read at this level.
        result.Reads.ExceptWith(result.Writes);
        return result;
    }

    /// <summary>
    /// Resolves an interface method to its in-solution implementations; non-interface
    /// methods pass through unchanged. Used by repo-body tracing so interface-typed
    /// dependencies (the common case — <c>IFooRepository</c>) still get scanned.
    /// </summary>
    private static async Task<List<IMethodSymbol>> ResolveConcreteMethodsAsync(
        IMethodSymbol method, Solution solution, CancellationToken ct)
    {
        var results = new List<IMethodSymbol>();
        if (method.ContainingType.TypeKind == TypeKind.Interface)
        {
            var impls = await SymbolFinder.FindImplementationsAsync(method, solution, cancellationToken: ct);
            foreach (var impl in impls.OfType<IMethodSymbol>())
            {
                if (IsTraceableInSolution(impl))
                    results.Add(impl);
            }
        }
        else if (IsTraceableInSolution(method))
        {
            results.Add(method);
        }
        return results;
    }

    /// <summary>
    /// Scan a single concrete method body for DbSet accesses and untraced repo calls.
    /// Accumulates into <paramref name="result"/> so callers can fan over multiple
    /// implementations of the same interface.
    /// </summary>
    private static async Task ScanRepoBodyIntoAsync(
        IMethodSymbol method,
        Solution solution,
        RepoBodyResult result,
        CancellationToken ct)
    {
        var (methodDecl, semanticModel) = await GetSyntaxAndModelAsync(method, solution, ct);
        if (methodDecl is null || semanticModel is null)
            return;
        if (methodDecl.Body is null && methodDecl.ExpressionBody is null)
            return;

        var repoInstanceMembers = CollectInstanceMembers(method.ContainingType);
        var bodyNodes = methodDecl.DescendantNodes().ToList();
        var saveChangesPresent = HasSaveChanges(bodyNodes);

        foreach (var (table, isWrite) in CollectDbSetAccesses(bodyNodes, semanticModel, saveChangesPresent))
        {
            if (isWrite) result.Writes.Add(table);
            else result.Reads.Add(table);
        }

        foreach (var invocation in bodyNodes.OfType<InvocationExpressionSyntax>())
        {
            var classification = ClassifyInvocation(invocation, semanticModel, method.ContainingType, repoInstanceMembers);
            if (classification.Kind == InvocationKind.DependencyCall && classification.IsRepoReceiver)
                result.UntracedRepoCalls.Add(classification.Display);
        }
    }

    /// <summary>
    /// Collect (table, isWrite) for every DbSet property access in a method body. Uses the
    /// semantic model so it works for DbSet access through fields, locals (incl.
    /// IDbContextFactory&lt;T&gt;.CreateDbContext results), parameters, etc.
    /// </summary>
    private static IEnumerable<(string Table, bool IsWrite)> CollectDbSetAccesses(
        IEnumerable<SyntaxNode> bodyNodes,
        SemanticModel semanticModel,
        bool saveChangesPresent)
    {
        foreach (var memberAccess in bodyNodes.OfType<MemberAccessExpressionSyntax>())
        {
            var symbolInfo = semanticModel.GetSymbolInfo(memberAccess);
            if (symbolInfo.Symbol is not IPropertySymbol prop || !DbContextAnalyzer.IsDbSetType(prop.Type))
                continue;

            // Confirm the receiver is a DbContext (avoid matching unrelated types that happen
            // to expose a DbSet-typed property — rare but possible).
            var receiverType = semanticModel.GetTypeInfo(memberAccess.Expression).Type;
            if (receiverType is null || !DbContextAnalyzer.IsDbContextType(receiverType))
                continue;

            var (write, _) = ClassifyDbSetAccess(memberAccess);
            yield return (prop.Name, write || saveChangesPresent);
        }
    }

    private static bool HasSaveChanges(IEnumerable<SyntaxNode> bodyNodes) =>
        bodyNodes.OfType<InvocationExpressionSyntax>().Any(inv =>
            inv.Expression is MemberAccessExpressionSyntax sma
            && (sma.Name.Identifier.Text == "SaveChangesAsync" || sma.Name.Identifier.Text == "SaveChanges"));

    private static async Task<(MethodDeclarationSyntax? MethodDecl, SemanticModel? SemanticModel)> GetSyntaxAndModelAsync(
        IMethodSymbol method, Solution solution, CancellationToken ct)
    {
        var declRef = method.DeclaringSyntaxReferences.FirstOrDefault();
        if (declRef is null) return (null, null);

        var node = await declRef.GetSyntaxAsync(ct);
        if (node is not MethodDeclarationSyntax methodDecl) return (null, null);

        var tree = methodDecl.SyntaxTree;
        var project = solution.Projects.FirstOrDefault(p => p.Documents.Any(d => d.FilePath == tree.FilePath));
        if (project is null) return (null, null);

        var compilation = await project.GetCompilationAsync(ct);
        if (compilation is null) return (null, null);

        return (methodDecl, compilation.GetSemanticModel(tree));
    }

    /// <summary>
    /// Walks up the syntax tree from a DbSet access (e.g. _db.Users) to determine if it's
    /// being mutated directly via Add/Update/Remove/etc. Returns (isWrite, reasonHint).
    /// </summary>
    private static (bool IsWrite, string? Reason) ClassifyDbSetAccess(MemberAccessExpressionSyntax dbsetAccess)
    {
        SyntaxNode? current = dbsetAccess;
        while (current is not null)
        {
            if (current.Parent is MemberAccessExpressionSyntax parentMa
                && parentMa.Expression == current
                && WriteApiNames.Contains(parentMa.Name.Identifier.Text))
            {
                return (true, parentMa.Name.Identifier.Text);
            }

            // Stop the climb at statement boundaries — we only want the immediate use-site chain.
            if (current.Parent is StatementSyntax)
                break;
            current = current.Parent;
        }

        return (false, null);
    }

    private enum InvocationKind
    {
        Ignored,
        DependencyCall,
        PrivateHelper,
        External
    }

    private record InvocationClassification(
        InvocationKind Kind,
        string Display,
        IMethodSymbol? Target,
        bool IsRepoReceiver = false);

    private static InvocationClassification ClassifyInvocation(
        InvocationExpressionSyntax invocation,
        SemanticModel semanticModel,
        INamedTypeSymbol containingType,
        Dictionary<string, ITypeSymbol> instanceMembers)
    {
        var symbolInfo = semanticModel.GetSymbolInfo(invocation);
        var targetSymbol = symbolInfo.Symbol as IMethodSymbol
                           ?? symbolInfo.CandidateSymbols.OfType<IMethodSymbol>().FirstOrDefault();

        switch (invocation.Expression)
        {
            case MemberAccessExpressionSyntax ma:
                return ClassifyMemberAccessInvocation(ma, targetSymbol, instanceMembers);

            case IdentifierNameSyntax id:
                {
                    // Bare invocation -> potentially a method on `this` (private/inherited helper).
                    var name = id.Identifier.Text;
                    if (targetSymbol is not null && SymbolEqualityComparer.Default.Equals(targetSymbol.ContainingType, containingType))
                    {
                        return new InvocationClassification(InvocationKind.PrivateHelper, $"{name} (private)", targetSymbol);
                    }
                    if (targetSymbol is not null && targetSymbol.IsStatic)
                    {
                        var owner = targetSymbol.ContainingType?.Name;
                        return new InvocationClassification(InvocationKind.External, owner is null ? name : $"{owner}.{name}", null);
                    }
                    return new InvocationClassification(InvocationKind.Ignored, name, null);
                }

            default:
                return new InvocationClassification(InvocationKind.Ignored, "", null);
        }
    }

    private static InvocationClassification ClassifyMemberAccessInvocation(
        MemberAccessExpressionSyntax ma,
        IMethodSymbol? targetSymbol,
        Dictionary<string, ITypeSymbol> instanceMembers)
    {
        var calledName = ma.Name.Identifier.Text;

        // Drop SaveChanges* from the call list (already implicit in DbSet write classification).
        if (calledName is "SaveChangesAsync" or "SaveChanges")
            return new InvocationClassification(InvocationKind.Ignored, "", null);

        if (TryGetInstanceReceiver(ma.Expression, instanceMembers, out var receiverName, out var receiverType))
        {
            // Calls on the DbContext field itself are EF/DbSet machinery — already covered.
            if (DbContextAnalyzer.IsDbContextType(receiverType))
                return new InvocationClassification(InvocationKind.Ignored, "", null);

            // LINQ over a DbSet (e.g. _db.Users.Where(...)) is also DbSet machinery.
            if (ReceiverChainStartsAtDbContext(ma.Expression, instanceMembers))
                return new InvocationClassification(InvocationKind.Ignored, "", null);

            var isRepo = IsRepoType(receiverType);
            return new InvocationClassification(
                InvocationKind.DependencyCall,
                $"_{TrimUnderscore(receiverName)}.{calledName}".Replace("__", "_"),
                targetSymbol,
                isRepo);
        }

        // Static external IO classes: File, Directory, HttpClient subtypes, etc. (best-effort by name).
        if (targetSymbol is not null && targetSymbol.IsStatic)
        {
            if (IsExternalIONamespace(targetSymbol.ContainingType))
                return new InvocationClassification(InvocationKind.External, $"{targetSymbol.ContainingType?.Name}.{calledName}", null);
            return new InvocationClassification(InvocationKind.Ignored, "", null);
        }

        // Instance call on something that isn't an instance field — e.g. local variable,
        // chained method result, or LINQ extension. Try to flag external IO via known types.
        if (targetSymbol?.ContainingType is { } owner2 && IsExternalIONamespace(owner2))
        {
            return new InvocationClassification(InvocationKind.External, $"{owner2.Name}.{calledName}", null);
        }

        return new InvocationClassification(InvocationKind.Ignored, "", null);
    }

    /// <summary>
    /// Issue #7/#8 receiver-type taxonomy: "repo" covers DbContext, IDbContextFactory&lt;T&gt;,
    /// and any type whose name ends with "Repository" (matches I*Repository and *Repository).
    /// </summary>
    private static bool IsRepoType(ITypeSymbol type)
    {
        if (DbContextAnalyzer.IsDbContextType(type)) return true;
        if (type is INamedTypeSymbol named && named.ConstructedFrom?.Name == "IDbContextFactory") return true;
        return type.Name.EndsWith("Repository", StringComparison.Ordinal);
    }

    private static bool TryGetInstanceReceiver(
        ExpressionSyntax expression,
        Dictionary<string, ITypeSymbol> instanceMembers,
        out string name,
        out ITypeSymbol type)
    {
        switch (expression)
        {
            case IdentifierNameSyntax id when instanceMembers.TryGetValue(id.Identifier.Text, out var t):
                name = id.Identifier.Text;
                type = t;
                return true;

            case MemberAccessExpressionSyntax ma when ma.Expression is ThisExpressionSyntax
                && instanceMembers.TryGetValue(ma.Name.Identifier.Text, out var t2):
                name = ma.Name.Identifier.Text;
                type = t2;
                return true;

            default:
                name = string.Empty;
                type = null!;
                return false;
        }
    }

    /// <summary>
    /// True when the expression is a member-access chain rooted in a DbContext instance
    /// (e.g. `_db.Users` or `_db.Users.AsNoTracking()`).
    /// </summary>
    private static bool ReceiverChainStartsAtDbContext(
        ExpressionSyntax expression,
        Dictionary<string, ITypeSymbol> instanceMembers)
    {
        var current = expression;
        while (true)
        {
            switch (current)
            {
                case InvocationExpressionSyntax inv:
                    current = inv.Expression;
                    break;
                case MemberAccessExpressionSyntax ma:
                    current = ma.Expression;
                    break;
                case IdentifierNameSyntax id:
                    return instanceMembers.TryGetValue(id.Identifier.Text, out var t) && DbContextAnalyzer.IsDbContextType(t);
                default:
                    return false;
            }
        }
    }

    private static bool IsExternalIONamespace(INamedTypeSymbol? type)
    {
        if (type is null) return false;
        var ns = type.ContainingNamespace?.ToDisplayString() ?? string.Empty;
        return ns.StartsWith("System.Net.Http", StringComparison.Ordinal)
            || ns.StartsWith("System.IO", StringComparison.Ordinal)
            || ns.StartsWith("Google.", StringComparison.Ordinal)
            || ns.StartsWith("Microsoft.AspNetCore.SignalR.Client", StringComparison.Ordinal);
    }

    private static bool IsTraceableInSolution(IMethodSymbol method)
    {
        return method.DeclaringSyntaxReferences.Length > 0
            && method.Locations.Any(l => l.IsInSource);
    }

    private static string TrimUnderscore(string name) => name.StartsWith('_') ? name[1..] : name;

    private static void WriteCompact(
        INamedTypeSymbol typeSymbol,
        List<MethodDownstreamReport> reports,
        int? totalBeforeLimit)
    {
        var header = $"{typeSymbol.ToDisplayString()} - {reports.Count}";
        if (totalBeforeLimit.HasValue)
            header += $" of {totalBeforeLimit.Value}";
        header += reports.Count == 1 ? " method" : " methods";
        header += ", downstream analysis";
        Console.WriteLine(header);

        foreach (var r in reports)
        {
            Console.WriteLine();
            Console.WriteLine(r.Method.Name);

            if (r.Calls.Count > 0)
                Console.WriteLine($"  Calls: {string.Join(", ", r.Calls.Distinct())}");

            var dbsetParts = new List<string>();
            if (r.DbSetWrites.Count > 0)
                dbsetParts.Add($"writes {FormatDbSetEntries(r.DbSetWrites)}");
            if (r.DbSetReads.Count > 0)
                dbsetParts.Add($"reads {FormatDbSetEntries(r.DbSetReads)}");
            if (dbsetParts.Count > 0)
                Console.WriteLine($"  DbSets: {string.Join("; ", dbsetParts)}");

            if (r.UntracedRepoCalls.Count > 0)
                Console.WriteLine($"  Untraced repo calls: {string.Join(", ", r.UntracedRepoCalls)}");

            if (r.External.Count > 0)
                Console.WriteLine($"  External: {string.Join(", ", r.External.Distinct())}");

            if (r.Calls.Count == 0 && dbsetParts.Count == 0 && r.External.Count == 0 && r.UntracedRepoCalls.Count == 0)
                Console.WriteLine("  (no outbound calls)");
        }
    }

    private static string FormatDbSetEntries(IEnumerable<(string Table, string? Via)> entries) =>
        string.Join(", ", entries.Select(e =>
            string.IsNullOrEmpty(e.Via) ? e.Table : $"{e.Table} (via {e.Via})"));

    private static void WriteJson(
        INamedTypeSymbol typeSymbol,
        List<MethodDownstreamReport> reports,
        int? totalBeforeLimit)
    {
        var payload = new
        {
            command = "audit-downstream",
            symbol = typeSymbol.ToDisplayString(),
            methods = reports.Select(r => new
            {
                name = r.Method.Name,
                calls = r.Calls.Distinct().ToArray(),
                dbSets = new
                {
                    reads = r.DbSetReads.Select(e => new { table = e.Table, via = e.Via }).ToArray(),
                    writes = r.DbSetWrites.Select(e => new { table = e.Table, via = e.Via }).ToArray(),
                    untracedRepoCalls = r.UntracedRepoCalls.Count > 0 ? r.UntracedRepoCalls.ToArray() : null
                },
                external = r.External.Distinct().ToArray()
            }).ToArray(),
            total = reports.Count,
            totalBeforeLimit
        };
        Console.WriteLine(JsonSerializer.Serialize(payload, JsonOptions));
    }

    private record MethodDownstreamReport(
        IMethodSymbol Method,
        List<string> Calls,
        List<(string Table, string? Via)> DbSetReads,
        List<(string Table, string? Via)> DbSetWrites,
        List<string> External,
        List<string> UntracedRepoCalls);

    private sealed class MethodTrace
    {
        public HashSet<string> Calls { get; } = new(StringComparer.Ordinal);
        public HashSet<(string Table, string? Via)> DbSetReads { get; } = new();
        public HashSet<(string Table, string? Via)> DbSetWrites { get; } = new();
        public HashSet<string> External { get; } = new(StringComparer.Ordinal);
        public HashSet<string> UntracedRepoCalls { get; } = new(StringComparer.Ordinal);
    }

    private sealed class RepoBodyResult
    {
        public HashSet<string> Reads { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Writes { get; } = new(StringComparer.Ordinal);
        public HashSet<string> UntracedRepoCalls { get; } = new(StringComparer.Ordinal);
    }
}
