using Microsoft.CodeAnalysis;

namespace Reforge;

// Section-architecture advisories plus the conservation anchors and helper candidates the
// baseline gate diffs against to catch score-driven consolidation. Nothing here scores: a
// section's shape (which interfaces it publishes, what its read methods return) is reported by
// section-shape, and the surface axis prices what the assembly exports rather than what a
// convention says it should export.
public sealed partial class SurfaceScoreEngine
{
    /// <summary>
    /// Escape-analysis advisory: a cross-section read-only use that could not be confirmed because
    /// the dependency escapes the consumer. Diagnostic only, no penalty.
    /// </summary>
    private static void EmitSectionAdvisories(SectionArchitecture arch, ScoreReport report)
    {
        foreach (var section in arch.Sections)
            foreach (var use in section.WriteSurfaceUnverified)
                report.Diagnostics.Add(new ScoreDiagnostic("info", "writeSurfaceUseUnverified",
                    $"{use.Caller} <- {use.Dependency}: read-only use unconfirmed (dependency escapes analysis); advisory only."));
    }

    /// <summary>
    /// Emits the section's conservation anchors: each canonical DTO (with its recursive member
    /// paths) and each read/full service interface (with method signatures + the surface points
    /// attributed to its methods). FQ-keyed by section so the Plan C gate can hold a refactor to a
    /// stable identity. Report-level — independent of any top-symbols display cap.
    /// </summary>
    private static List<ConservationAnchor> BuildConservationAnchors(SectionArchitecture arch, ScoreReport report)
    {
        var anchors = new List<ConservationAnchor>();
        foreach (var section in arch.Sections)
        {
            foreach (var dto in new[] { section.PrimaryInfoDto, section.SettingsInfoDto, section.CacheDto })
            {
                if (dto is null) continue;
                anchors.Add(new ConservationAnchor(
                    $"{section.Name}::{dto.Display}", section.Name, dto.Role,
                    dto.Paths, Array.Empty<ConservationAnchorMethod>(),
                    new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)));
            }

            foreach (var iface in arch.InterfaceAnchors.Where(i => string.Equals(i.Section, section.Name, StringComparison.OrdinalIgnoreCase)))
            {
                var methodNames = iface.Methods.Select(m => m.Name).ToHashSet(StringComparer.Ordinal);
                anchors.Add(new ConservationAnchor(
                    $"{section.Name}::{iface.Display}", section.Name, iface.Role,
                    Array.Empty<string>(),
                    iface.Methods.Select(m => new ConservationAnchorMethod(m.Name, m.Returns)).ToList(),
                    AttributePointsByRule(report, section.Name, methodNames)));
            }
        }
        return anchors;
    }

    /// <summary>
    /// Collects stateless-sink classes (static class, or a class with no instance fields that is not
    /// backed by a source interface) and their public method names. Broad by design — the spec wants
    /// any new stateless sink to count as a helper-extraction destination, not one narrow shape.
    /// </summary>
    private static List<HelperCandidate> BuildHelperCandidates(List<ClassifiedType> classified)
    {
        var helpers = new List<HelperCandidate>();
        foreach (var c in classified)
        {
            if (c.Type.TypeKind != TypeKind.Class) continue;
            bool hasInstanceField = c.Type.GetMembers().OfType<IFieldSymbol>().Any(f => !f.IsStatic && !f.IsImplicitlyDeclared);
            bool interfaceBacked = c.Type.AllInterfaces.Any(i => i.Locations.Any(l => l.IsInSource));
            bool stateless = c.Type.IsStatic || (!hasInstanceField && !interfaceBacked);
            if (!stateless) continue;

            var methods = c.Type.GetMembers().OfType<IMethodSymbol>()
                .Where(m => m.MethodKind == MethodKind.Ordinary && m.AssociatedSymbol is null
                            && !m.IsImplicitlyDeclared && m.DeclaredAccessibility == Accessibility.Public)
                .Select(m => m.Name).ToList();
            if (methods.Count == 0) continue;
            helpers.Add(new HelperCandidate(c.Type.ToDisplayString(), methods));
        }
        return helpers;
    }

    /// <summary>Best-effort: sum the points of entries in a group whose symbol is one of the anchor's methods.</summary>
    private static Dictionary<string, int> AttributePointsByRule(ScoreReport report, string group, HashSet<string> methodNames)
    {
        var byRule = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (report.Groups.TryGetValue(group, out var g))
            foreach (var e in g.Entries)
                if (methodNames.Contains(e.Symbol))
                    byRule[e.Rule] = byRule.GetValueOrDefault(e.Rule) + e.Points;
        return byRule;
    }
}
