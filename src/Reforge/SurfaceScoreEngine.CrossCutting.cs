using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Reforge;

// Cross-cutting rules that key off the solution rather than a single type: duplicate DbSet
// owners, DI registrations, and interfaces with exactly one implementation.
public sealed partial class SurfaceScoreEngine
{
    /// <summary>
    /// A table belongs to the section that declares its <c>DbSet</c> — i.e. the section of the
    /// declaring <c>DbContext</c>'s assembly. Any class in another section that reads or writes
    /// that DbSet is a second owner. Ownership is read off the model, never off a config map, so
    /// it cannot drift; when a section extracts its own context the ownership moves with it.
    /// </summary>
    private void ScoreDuplicateDbSetOwners(
        List<ClassifiedType> classified,
        Solution solution,
        ScoreReport report,
        CancellationToken ct)
    {
        var weight = _config.Weight("duplicateDbSetOwner");
        if (weight == 0) return;

        var ownerMap = DbSetOwnersByDeclaringContext(classified);
        if (ownerMap.Count == 0) return;

        var alreadyReported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var c in classified)
        {
            if (c.Type.TypeKind != TypeKind.Class) continue;

            // Cheap pre-filter: only classes with a DbContext constructor param touch DbSets.
            var hasDbContext = c.Type.Constructors.Any(ctor =>
                ctor.Parameters.Any(p => DbContextAnalyzer.IsDbContextType(p.Type)));
            if (!hasDbContext) continue;

            var accesses = DbContextAnalyzer.FindDbSetAccessesAsync(c.Type, solution, ct).GetAwaiter().GetResult();
            var distinct = accesses.Select(a => a.DbSetName).Distinct(StringComparer.OrdinalIgnoreCase);

            foreach (var dbSet in distinct)
            {
                if (!ownerMap.TryGetValue(dbSet, out var owner)) continue;
                if (string.Equals(owner, c.Group, StringComparison.OrdinalIgnoreCase)) continue;

                var key = $"{c.Type.ToDisplayString()}|{dbSet}";
                if (!alreadyReported.Add(key)) continue;

                AddEntry(report, c.Group, "duplicateDbSetOwner", weight, c.Type, c.File, c.Line,
                    $"{c.Type.Name} touches {dbSet} (owned by {owner})");
                report.DuplicateOwners.Add($"{c.Type.ToDisplayString()} touches {dbSet} (owner: {owner})");
            }
        }
    }

    /// <summary>
    /// DbSet property name -> owning section, derived from every source-declared DbContext. When
    /// two contexts in different sections expose the same DbSet name the table has no single owner,
    /// so it is dropped rather than attributed to whichever context was enumerated first.
    /// </summary>
    private static Dictionary<string, string> DbSetOwnersByDeclaringContext(List<ClassifiedType> classified)
    {
        var owners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var contested = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var c in classified)
        {
            if (c.Type.TypeKind != TypeKind.Class) continue;
            if (!DbContextAnalyzer.IsDbContextType(c.Type)) continue;

            foreach (var member in c.Type.GetMembers())
            {
                if (member is not IPropertySymbol prop) continue;
                if (!DbContextAnalyzer.IsDbSetType(prop.Type)) continue;

                if (owners.TryGetValue(prop.Name, out var existing)
                    && !string.Equals(existing, c.Group, StringComparison.OrdinalIgnoreCase))
                {
                    contested.Add(prop.Name);
                    continue;
                }
                owners[prop.Name] = c.Group;
            }
        }

        foreach (var name in contested) owners.Remove(name);
        return owners;
    }

    private async Task ScoreDiRegistrationsAsync(
        Solution solution,
        Dictionary<string, ClassifiedType> classifiedByDisplay,
        ScoreReport report,
        CancellationToken ct)
    {
        var weight = _config.Weight("diRegistration");
        if (weight == 0) return;

        // Heuristic: a DI registration is a call to AddSingleton/AddScoped/AddTransient.
        // We attribute each registration to the group of the *registered service* (the
        // first type argument) so that "Tickets registers TicketsService" credits Tickets.
        var registrationMethods = new HashSet<string>(StringComparer.Ordinal)
        {
            "AddSingleton", "AddScoped", "AddTransient", "TryAddSingleton", "TryAddScoped", "TryAddTransient"
        };

        foreach (var project in solution.Projects)
        {
            if (project.Name.Contains("Test", StringComparison.OrdinalIgnoreCase)) continue;
            var compilation = await project.GetCompilationAsync(ct);
            if (compilation is null) continue;

            foreach (var tree in compilation.SyntaxTrees)
            {
                var root = await tree.GetRootAsync(ct);
                var model = compilation.GetSemanticModel(tree);

                foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    string? methodName = invocation.Expression switch
                    {
                        MemberAccessExpressionSyntax ma => ma.Name is GenericNameSyntax g ? g.Identifier.Text : ma.Name.Identifier.Text,
                        GenericNameSyntax g => g.Identifier.Text,
                        _ => null
                    };
                    if (methodName is null || !registrationMethods.Contains(methodName)) continue;

                    var genericName = invocation.Expression switch
                    {
                        MemberAccessExpressionSyntax ma2 => ma2.Name as GenericNameSyntax,
                        GenericNameSyntax g2 => g2,
                        _ => null
                    };
                    if (genericName is null || genericName.TypeArgumentList.Arguments.Count == 0) continue;

                    var typeArg = genericName.TypeArgumentList.Arguments[0];
                    var typeInfo = model.GetSymbolInfo(typeArg).Symbol as INamedTypeSymbol;
                    if (typeInfo is null) continue;

                    if (!classifiedByDisplay.TryGetValue(typeInfo.ToDisplayString(), out var c)) continue;

                    var loc = invocation.GetLocation();
                    var ls = loc.GetLineSpan();
                    var file = LocationHelper.NormalizePath(ls.Path, _solutionDirectory);
                    AddEntry(report, c.Group, "diRegistration", weight, typeInfo, file, ls.StartLinePosition.Line + 1,
                        $"{methodName}<{typeInfo.Name}>");
                }
            }
        }
    }

    private void ScoreOneImplementationInterfaces(List<ClassifiedType> classified, ScoreReport report)
    {
        var weight = _config.Weight("oneImplementationInterface");
        if (weight == 0) return;

        // For every classified interface, count how many classified classes implement it.
        var interfaces = classified.Where(c => c.Type.TypeKind == TypeKind.Interface).ToList();
        var classes = classified.Where(c => c.Type.TypeKind == TypeKind.Class).ToList();
        if (interfaces.Count == 0 || classes.Count == 0) return;

        foreach (var iface in interfaces)
        {
            // An internal interface with one implementation is an implementation choice, not a
            // published abstraction nobody varies.
            if (!iface.IsExported) continue;

            int implCount = 0;
            ClassifiedType? singleImpl = null;
            foreach (var cls in classes)
            {
                if (cls.Type.AllInterfaces.Any(i => SymbolEqualityComparer.Default.Equals(i, iface.Type)))
                {
                    implCount++;
                    singleImpl = cls;
                    if (implCount > 1) break;
                }
            }
            if (implCount == 1 && singleImpl is not null)
            {
                AddEntry(report, iface.Group, "oneImplementationInterface", weight, iface.Type, iface.File, iface.Line,
                    $"only {singleImpl.Type.Name} implements {iface.Type.Name}");
            }
        }
    }
}
