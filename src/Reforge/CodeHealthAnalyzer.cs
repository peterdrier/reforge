using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Reforge;

public record TypeHealthReport(
    string Name,
    string QualifiedName,
    string File,
    int Line,
    int Score,              // 0-100 composite risk score
    int Lines,              // total source lines in the type
    int MethodCount,
    int MaxCyclomaticComplexity,
    string MaxComplexityMethod, // name of the most complex method
    int DependencyCount,    // efferent coupling (Ce) — types this depends on
    int DependentCount,     // afferent coupling (Ca) — types that depend on this
    double Instability,     // Ce / (Ca + Ce), 0=stable, 1=unstable
    double CohesionScore,   // 0-1, higher = more cohesive
    int FieldClusterCount   // for LCOM explanation
);

public static class CodeHealthAnalyzer
{
    public static async Task<List<TypeHealthReport>> AnalyzeAsync(
        Solution solution, string? namespaceFilter, CancellationToken ct)
    {
        // 1. Collect all source-defined named types.
        //    sourceTypeNames includes ALL source types (interfaces, enums, etc.) for coupling lookup.
        //    allTypes includes only concrete types that will get health reports.
        var allTypes = new List<(INamedTypeSymbol Symbol, SemanticModel Model, SyntaxNode DeclNode)>();
        var sourceTypeNames = new HashSet<string>();

        foreach (var project in solution.Projects)
        {
            if (ct.IsCancellationRequested) break;

            // Skip test projects
            if (project.Name.Contains("Test", StringComparison.OrdinalIgnoreCase))
                continue;

            var compilation = await project.GetCompilationAsync(ct);
            if (compilation is null) continue;

            foreach (var syntaxTree in compilation.SyntaxTrees)
            {
                // Skip generated files in obj/ directories
                var filePath = syntaxTree.FilePath ?? "";
                if (filePath.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
                    filePath.Contains("/obj/"))
                    continue;

                var model = compilation.GetSemanticModel(syntaxTree);
                var root = await syntaxTree.GetRootAsync(ct);

                foreach (var typeDecl in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
                {
                    var symbol = model.GetDeclaredSymbol(typeDecl, ct);
                    if (symbol is null) continue;
                    if (symbol.IsImplicitlyDeclared) continue;

                    // Register ALL source types for coupling lookup
                    sourceTypeNames.Add(symbol.ToDisplayString());

                    // Only analyze concrete non-enum types for health reports
                    if (symbol.TypeKind == TypeKind.Interface) continue;
                    if (symbol.TypeKind == TypeKind.Enum) continue;
                    if (symbol.IsAbstract && symbol.GetMembers().OfType<IMethodSymbol>()
                            .All(m => m.IsAbstract || m.IsImplicitlyDeclared)) continue;

                    // Namespace filter (only for analyzed types, not for coupling lookup)
                    if (namespaceFilter is not null)
                    {
                        var ns = symbol.ContainingNamespace?.ToDisplayString() ?? "";
                        if (!ns.StartsWith(namespaceFilter, StringComparison.OrdinalIgnoreCase))
                            continue;
                    }

                    allTypes.Add((symbol, model, typeDecl));
                }
            }
        }

        // Deduplicate by qualified name (same type may appear in multiple compilations for
        // multi-project solutions where projects reference each other)
        var deduped = allTypes
            .GroupBy(t => t.Symbol.ToDisplayString())
            .Select(g => g.First())
            .ToList();

        // 2. Build dependency map (efferent coupling)
        //    key = qualified type name, value = set of qualified type names it depends on
        var dependsOn = new Dictionary<string, HashSet<string>>();
        foreach (var (symbol, model, declNode) in deduped)
        {
            var key = symbol.ToDisplayString();
            var deps = ComputeEfferentDependencies(symbol, sourceTypeNames);
            dependsOn[key] = deps;
        }

        // 3. Invert to get afferent coupling (who depends on me)
        var dependedOnBy = new Dictionary<string, HashSet<string>>();
        foreach (var name in sourceTypeNames)
            dependedOnBy[name] = new HashSet<string>();

        foreach (var (typeName, deps) in dependsOn)
        {
            foreach (var dep in deps)
            {
                if (dependedOnBy.TryGetValue(dep, out var set))
                    set.Add(typeName);
            }
        }

        // 3b. Propagate interface dependents to implementations.
        // In DI codebases, types depend on IFoo, not Foo. Transfer IFoo's dependents to Foo.
        foreach (var (symbol, _, _) in deduped)
        {
            var implName = symbol.ToDisplayString();
            foreach (var iface in symbol.Interfaces)
            {
                var ifaceName = iface.ToDisplayString();
                if (dependedOnBy.TryGetValue(ifaceName, out var ifaceDeps))
                {
                    if (!dependedOnBy.ContainsKey(implName))
                        dependedOnBy[implName] = new HashSet<string>();
                    foreach (var dep in ifaceDeps)
                        dependedOnBy[implName].Add(dep);
                }
            }
        }

        // 4. Compute per-type metrics
        var solutionDir = LocationHelper.GetSolutionDirectory(solution);
        var reports = new List<TypeHealthReport>();

        foreach (var (symbol, model, declNode) in deduped)
        {
            if (ct.IsCancellationRequested) break;

            var qualifiedName = symbol.ToDisplayString();

            // Lines
            var lineSpan = declNode.GetLocation().GetLineSpan();
            int lines = lineSpan.EndLinePosition.Line - lineSpan.StartLinePosition.Line + 1;

            // Methods and cyclomatic complexity
            var methods = symbol.GetMembers().OfType<IMethodSymbol>()
                .Where(m => !m.IsImplicitlyDeclared && m.MethodKind is MethodKind.Ordinary or MethodKind.Constructor)
                .ToList();

            int methodCount = methods.Count;
            int maxCC = 0;
            string maxCCMethod = "";

            foreach (var method in methods)
            {
                var methodDecl = method.DeclaringSyntaxReferences.FirstOrDefault();
                if (methodDecl is null) continue;
                var methodNode = await methodDecl.GetSyntaxAsync(ct);

                // Find body (block or expression body)
                SyntaxNode? body = methodNode switch
                {
                    MethodDeclarationSyntax m => (SyntaxNode?)m.Body ?? m.ExpressionBody,
                    ConstructorDeclarationSyntax c => (SyntaxNode?)c.Body ?? c.ExpressionBody,
                    _ => null
                };

                if (body is null) continue;

                int cc = ComputeCyclomaticComplexity(body);
                if (cc > maxCC)
                {
                    maxCC = cc;
                    maxCCMethod = method.Name;
                }
            }

            // Skip small types (< 5 methods or < 50 lines)
            if (methodCount < 5 && lines < 50)
                continue;

            // Coupling
            var ce = dependsOn.TryGetValue(qualifiedName, out var ceSet) ? ceSet.Count : 0;
            var ca = dependedOnBy.TryGetValue(qualifiedName, out var caSet) ? caSet.Count : 0;
            double instability = (ca + ce) > 0 ? (double)ce / (ca + ce) : 0;

            // Cohesion (LCOM4-like)
            var (cohesion, clusterCount) = await ComputeCohesionAsync(symbol, model, declNode, ct);

            // Composite score
            int score = ComputeCompositeScore(maxCC, lines, ce, ca, cohesion);

            // File location
            var location = symbol.Locations.FirstOrDefault(l => l.IsInSource);
            string file = "";
            int line = 0;
            if (location is not null)
            {
                file = LocationHelper.NormalizePath(location.GetLineSpan().Path, solutionDir);
                line = location.GetLineSpan().StartLinePosition.Line + 1;
            }

            reports.Add(new TypeHealthReport(
                Name: symbol.Name,
                QualifiedName: qualifiedName,
                File: file,
                Line: line,
                Score: score,
                Lines: lines,
                MethodCount: methodCount,
                MaxCyclomaticComplexity: maxCC,
                MaxComplexityMethod: maxCCMethod,
                DependencyCount: ce,
                DependentCount: ca,
                Instability: instability,
                CohesionScore: cohesion,
                FieldClusterCount: clusterCount
            ));
        }

        return reports;
    }

    private static int ComputeCyclomaticComplexity(SyntaxNode methodBody)
    {
        int complexity = 1; // base
        foreach (var node in methodBody.DescendantNodes())
        {
            complexity += node switch
            {
                IfStatementSyntax => 1,
                ElseClauseSyntax { Statement: IfStatementSyntax } => 0, // already counted by the if
                CaseSwitchLabelSyntax => 1,
                CasePatternSwitchLabelSyntax => 1,
                SwitchExpressionArmSyntax => 1,
                ConditionalExpressionSyntax => 1,
                ForStatementSyntax => 1,
                ForEachStatementSyntax => 1,
                WhileStatementSyntax => 1,
                DoStatementSyntax => 1,
                CatchClauseSyntax => 1,
                BinaryExpressionSyntax b when b.IsKind(SyntaxKind.LogicalAndExpression) => 1,
                BinaryExpressionSyntax b when b.IsKind(SyntaxKind.LogicalOrExpression) => 1,
                BinaryExpressionSyntax b when b.IsKind(SyntaxKind.CoalesceExpression) => 1,
                ConditionalAccessExpressionSyntax => 1,
                _ => 0
            };
        }
        return complexity;
    }

    private static HashSet<string> ComputeEfferentDependencies(
        INamedTypeSymbol typeSymbol, HashSet<string> sourceTypeNames)
    {
        var deps = new HashSet<string>();
        var selfName = typeSymbol.ToDisplayString();

        void TryAdd(ITypeSymbol? type)
        {
            if (type is null) return;

            // Unwrap generic types to get the underlying named type
            if (type is INamedTypeSymbol named)
            {
                var display = named.OriginalDefinition.ToDisplayString();
                if (display != selfName && sourceTypeNames.Contains(display))
                    deps.Add(display);

                // Also check type arguments
                foreach (var arg in named.TypeArguments)
                    TryAdd(arg);
            }
            else if (type is IArrayTypeSymbol array)
            {
                TryAdd(array.ElementType);
            }
        }

        // Constructor parameters
        foreach (var ctor in typeSymbol.Constructors)
        {
            if (ctor.IsImplicitlyDeclared) continue;
            foreach (var param in ctor.Parameters)
                TryAdd(param.Type);
        }

        // Fields
        foreach (var field in typeSymbol.GetMembers().OfType<IFieldSymbol>())
        {
            if (field.IsImplicitlyDeclared) continue;
            TryAdd(field.Type);
        }

        // Properties
        foreach (var prop in typeSymbol.GetMembers().OfType<IPropertySymbol>())
        {
            if (prop.IsImplicitlyDeclared) continue;
            TryAdd(prop.Type);
        }

        // Method return types and parameters
        foreach (var method in typeSymbol.GetMembers().OfType<IMethodSymbol>())
        {
            if (method.IsImplicitlyDeclared) continue;
            if (method.MethodKind is not (MethodKind.Ordinary or MethodKind.Constructor)) continue;
            TryAdd(method.ReturnType);
            foreach (var param in method.Parameters)
                TryAdd(param.Type);
        }

        return deps;
    }

    private static async Task<(double Score, int ClusterCount)> ComputeCohesionAsync(
        INamedTypeSymbol typeSymbol, SemanticModel model, SyntaxNode declNode, CancellationToken ct)
    {
        // Get instance fields and properties (backing fields for auto-properties show up as property symbols)
        var instanceFields = typeSymbol.GetMembers()
            .Where(m => m is IFieldSymbol f && !f.IsStatic && !f.IsImplicitlyDeclared
                     || m is IPropertySymbol p && !p.IsStatic && !p.IsImplicitlyDeclared && !p.IsIndexer)
            .Select(m => m.Name)
            .ToHashSet();

        if (instanceFields.Count == 0)
            return (1.0, 1); // No fields = fully cohesive by definition

        // Get instance methods
        var instanceMethods = typeSymbol.GetMembers().OfType<IMethodSymbol>()
            .Where(m => !m.IsStatic && !m.IsImplicitlyDeclared
                        && m.MethodKind is MethodKind.Ordinary or MethodKind.Constructor)
            .ToList();

        if (instanceMethods.Count <= 1)
            return (1.0, 1); // 0-1 methods = trivially cohesive

        // For each method, find which fields it accesses
        var methodFieldAccess = new Dictionary<string, HashSet<string>>();

        foreach (var method in instanceMethods)
        {
            var fieldSet = new HashSet<string>();
            var methodRef = method.DeclaringSyntaxReferences.FirstOrDefault();
            if (methodRef is null) continue;

            var methodNode = await methodRef.GetSyntaxAsync(ct);
            SyntaxNode? body = methodNode switch
            {
                MethodDeclarationSyntax m => (SyntaxNode?)m.Body ?? m.ExpressionBody,
                ConstructorDeclarationSyntax c => (SyntaxNode?)c.Body ?? c.ExpressionBody,
                _ => null
            };
            if (body is null) continue;

            foreach (var identifier in body.DescendantNodes().OfType<IdentifierNameSyntax>())
            {
                var symbolInfo = model.GetSymbolInfo(identifier);
                var sym = symbolInfo.Symbol;

                if (sym is IFieldSymbol field &&
                    SymbolEqualityComparer.Default.Equals(field.ContainingType, typeSymbol) &&
                    !field.IsStatic)
                {
                    fieldSet.Add(field.Name);
                }
                else if (sym is IPropertySymbol prop &&
                         SymbolEqualityComparer.Default.Equals(prop.ContainingType, typeSymbol) &&
                         !prop.IsStatic)
                {
                    fieldSet.Add(prop.Name);
                }
            }

            if (fieldSet.Count > 0)
                methodFieldAccess[method.Name] = fieldSet;
        }

        if (methodFieldAccess.Count <= 1)
            return (1.0, 1);

        // Build adjacency graph: two methods are connected if they share at least one field
        var methodNames = methodFieldAccess.Keys.ToList();
        var adjacency = new Dictionary<string, HashSet<string>>();
        foreach (var name in methodNames)
            adjacency[name] = new HashSet<string>();

        for (int i = 0; i < methodNames.Count; i++)
        {
            for (int j = i + 1; j < methodNames.Count; j++)
            {
                var a = methodNames[i];
                var b = methodNames[j];
                if (methodFieldAccess[a].Overlaps(methodFieldAccess[b]))
                {
                    adjacency[a].Add(b);
                    adjacency[b].Add(a);
                }
            }
        }

        // Count connected components via BFS
        var visited = new HashSet<string>();
        int components = 0;
        foreach (var name in methodNames)
        {
            if (visited.Contains(name)) continue;
            components++;
            var queue = new Queue<string>();
            queue.Enqueue(name);
            visited.Add(name);
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                foreach (var neighbor in adjacency[current])
                {
                    if (visited.Add(neighbor))
                        queue.Enqueue(neighbor);
                }
            }
        }

        double score = 1.0 / components;
        return (score, components);
    }

    private static int ComputeCompositeScore(int maxCC, int lines, int ce, int ca, double cohesion)
    {
        double score = 0;
        score += Math.Min(maxCC / 30.0 * 25, 25);       // max 25 points from complexity
        score += Math.Min(lines / 1000.0 * 20, 20);      // max 20 points from size
        score += Math.Min(ce / 15.0 * 20, 20);            // max 20 points from efferent coupling
        score += Math.Min(ca / 20.0 * 15, 15);            // max 15 points from afferent coupling
        score += (1 - cohesion) * 20;                      // max 20 points from low cohesion
        return (int)Math.Round(Math.Min(score, 100));
    }
}
