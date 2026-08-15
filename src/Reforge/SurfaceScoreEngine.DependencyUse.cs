using Microsoft.CodeAnalysis;

namespace Reforge;

// Pass 2 — dependency use: what a class reaches for through its constructor. Charges for the
// coupling a use creates, whether or not the consumer itself is exported.
public sealed partial class SurfaceScoreEngine
{
    /// <summary>
    /// Deliberately NOT gated on <see cref="ClassifiedType.IsExported"/>, unlike the durable-surface
    /// rules. This pass charges for a <b>use</b>, not a declaration: an internal class injecting
    /// another section's repository still forces the assembly reference and still calls across the
    /// boundary. Marking the consumer internal changes nothing about that coupling, so gating here
    /// would have turned "make it internal" into a way to shed the penalty for free — the same
    /// gaming the effective-accessibility rule exists to close. Same reasoning for
    /// writeCapableInterfaceUsedReadOnly, crossSectionWriteSurface, duplicateDbSetOwner, and
    /// diRegistration.
    /// </summary>
    private void ScoreDependencyUse(
        List<ClassifiedType> classified,
        Dictionary<string, ClassifiedType> typesByDisplay,
        ScoreReport report)
    {
        foreach (var c in classified)
        {
            if (c.Type.TypeKind != TypeKind.Class) continue;

            foreach (var ctor in c.Type.Constructors)
            {
                if (ctor.IsImplicitlyDeclared) continue;
                foreach (var param in ctor.Parameters)
                {
                    var depDisplay = SolutionClassifier.TypeKey(param.Type);
                    if (!typesByDisplay.TryGetValue(depDisplay, out var dep)) continue;

                    // Same-group dependencies don't cost anything (or are zero-weighted).
                    var sameGroup = string.Equals(dep.Group, c.Group, StringComparison.OrdinalIgnoreCase);

                    string? rule = null;
                    if (dep.Tags.Contains("repositoryInterface") || dep.Tags.Contains("repositoryImplementation"))
                        rule = sameGroup ? null : "crossSectionRepository";
                    else if (dep.Tags.Contains("readServiceInterface"))
                        rule = sameGroup ? "sameSectionReadService" : "crossSectionReadInterface";
                    else if (dep.Tags.Contains("fullServiceInterface") || dep.Tags.Contains("applicationService"))
                        rule = sameGroup ? null : "crossSectionFullService";

                    if (rule is null) continue;
                    var weight = _config.Weight(rule);
                    if (weight == 0) continue;

                    var loc = ctor.Locations.FirstOrDefault(l => l.IsInSource);
                    var (file, line) = LocateMember(loc, c);
                    AddEntry(report, c.Group, rule, weight, c.Type, file, line,
                        $"{c.Type.Name} <- {param.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}");
                }
            }
        }
    }
}
