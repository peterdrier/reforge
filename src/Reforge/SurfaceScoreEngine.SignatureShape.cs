using Microsoft.CodeAnalysis;

namespace Reforge;

// Pass 3 — signature shape: parameter overflow, boolean parameters, tuple returns, options
// bags, and dashboard/admin naming. The shape of a published signature, not its body.
public sealed partial class SurfaceScoreEngine
{
    private async Task ScoreInternalShape(List<ClassifiedType> classified, ScoreReport report, CancellationToken ct)
    {
        var overflowWeight = _config.Weight("methodParameterOverflow");
        var boolWeight = _config.Weight("booleanParameter");
        var tupleWeight = _config.Weight("tupleReturn");
        var optionsBagWeight = _config.Weight("optionsBag");
        var nameWeight = _config.Weight("dashboardAdminPageName");

        foreach (var c in classified)
        {
            // These rules charge for the shape of a published signature (param count, bool flags,
            // tuple returns). An internal type publishes no signature.
            if (!c.IsExported) continue;
            // Only score shape for code-bearing types (skip pure DTOs).
            if (c.Tags.Contains("dto") && !c.Tags.Contains("applicationService")) continue;

            foreach (var member in c.Type.GetMembers())
            {
                if (member is not IMethodSymbol m) continue;
                if (m.MethodKind != MethodKind.Ordinary) continue;
                if (m.AssociatedSymbol is not null) continue;
                if (m.IsImplicitlyDeclared) continue;
                if (m.DeclaredAccessibility != Accessibility.Public) continue;

                var loc = m.Locations.FirstOrDefault(l => l.IsInSource);
                var (file, line) = LocateMember(loc, c);

                // Param-count overflow (after 2).
                if (m.Parameters.Length > 2 && overflowWeight > 0)
                {
                    var extra = m.Parameters.Length - 2;
                    AddEntry(report, c.Group, "methodParameterOverflow", extra * overflowWeight, m, file, line,
                        $"{m.Name}({m.Parameters.Length} params)");
                }

                // Bool parameters.
                foreach (var p in m.Parameters)
                {
                    if (p.Type.SpecialType == SpecialType.System_Boolean && boolWeight > 0)
                        AddEntry(report, c.Group, "booleanParameter", boolWeight, m, file, line, $"{m.Name}({p.Name})");
                }

                // Tuple return.
                if (tupleWeight > 0 && IsTupleType(UnwrapTaskLike(m.ReturnType)))
                    AddEntry(report, c.Group, "tupleReturn", tupleWeight, m, file, line, m.Name);

                // Options-bag: single param whose type carries many unrelated public properties.
                if (optionsBagWeight > 0 && m.Parameters.Length == 1 && IsOptionsBag(m.Parameters[0].Type))
                    AddEntry(report, c.Group, "optionsBag", optionsBagWeight, m, file, line, m.Name);

                // Naming smell: ForDashboard / ForAdmin / ForPage / Dashboard / Admin in the method name.
                if (nameWeight > 0 && HasDashboardAdminPageName(m.Name))
                    AddEntry(report, c.Group, "dashboardAdminPageName", nameWeight, m, file, line, m.Name);
            }
        }

        await Task.CompletedTask;
    }

    private static ITypeSymbol UnwrapTaskLike(ITypeSymbol t)
    {
        if (t is INamedTypeSymbol n && n.IsGenericType)
        {
            var name = n.OriginalDefinition.ToDisplayString();
            if (name is "System.Threading.Tasks.Task<TResult>" or "System.Threading.Tasks.ValueTask<TResult>")
                return n.TypeArguments[0];
        }
        return t;
    }

    private static bool IsTupleType(ITypeSymbol t)
    {
        if (t is INamedTypeSymbol n && n.IsTupleType) return true;
        return false;
    }

    private static bool IsOptionsBag(ITypeSymbol t)
    {
        if (t is not INamedTypeSymbol n) return false;
        if (n.SpecialType != SpecialType.None) return false;
        if (n.ContainingNamespace?.ToDisplayString().StartsWith("System", StringComparison.Ordinal) == true) return false;
        // Framework / third-party types (Roslyn ITypeSymbol, MediatR commands, etc.) shouldn't
        // count — only flag the user's own types.
        if (!n.Locations.Any(l => l.IsInSource)) return false;
        var publicProps = n.GetMembers().OfType<IPropertySymbol>()
            .Count(p => p.DeclaredAccessibility == Accessibility.Public);
        return publicProps >= 4 && n.Name.EndsWith("Options", StringComparison.Ordinal)
            || publicProps >= 6;
    }

    private static bool HasDashboardAdminPageName(string name)
    {
        return name.Contains("Dashboard", StringComparison.Ordinal)
            || name.Contains("ForAdmin", StringComparison.Ordinal)
            || name.Contains("ForPage", StringComparison.Ordinal)
            || name.Contains("AdminPage", StringComparison.Ordinal);
    }
}
