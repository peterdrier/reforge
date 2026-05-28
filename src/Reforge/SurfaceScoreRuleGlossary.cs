namespace Reforge;

/// <summary>
/// One-sentence factual description of each surface-score rule. Surfaced in the
/// report (filtered to only rules that fired) so AI agents and human readers can
/// understand what each rule key represents without consulting external docs.
///
/// Descriptions intentionally state WHAT triggers the rule, not WHAT TO DO about it.
/// Refactoring is the agent's judgement call — generic advice would be followed too
/// blindly and miss the point of the finding. The one exception is the per-entry
/// <c>detail</c> field, which can carry concrete remediation hints (e.g. the read
/// interface to swap to) because those derive from the specific symbol pair, not
/// from generic guidance.
/// </summary>
public static class SurfaceScoreRuleGlossary
{
    public static readonly IReadOnlyDictionary<string, string> Descriptions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        // Durable surface
        ["dtoScalarProperty"] = "A public scalar property on a type classified as a DTO.",
        ["dtoCollectionProperty"] = "A public collection-typed property on a DTO.",
        ["dtoNestedProperty"] = "A public property on a DTO whose type is another non-primitive (often another DTO).",
        ["publicDtoType"] = "A public type classified as a DTO with at least one public property and no business-logic methods.",
        ["applicationServiceMethod"] = "A public method on a concrete application-service class.",
        ["readServiceInterfaceMethod"] = "A method declared on a read-only service interface.",
        ["fullServiceInterfaceMethod"] = "A method declared on a full (read+write) service interface.",
        ["repositoryInterfaceMethod"] = "A method declared on a repository interface.",
        ["repositoryImplementationMethod"] = "A public method on a repository implementation class.",
        ["newRepositoryInterface"] = "A repository interface type — each is durable infrastructure surface.",
        ["newRepositoryImplementation"] = "A repository implementation class.",
        ["diRegistration"] = "A dependency-injection registration (AddSingleton/AddScoped/AddTransient/TryAdd*) of a classified type.",
        ["controllerAction"] = "A public action method on a controller.",
        ["backgroundJob"] = "A background job, hosted service, or worker class.",
        ["duplicateDbSetOwner"] = "A class reads or writes a DbSet that is owned (per config) by a different section.",
        ["canonicalReadDtoReturn"] = "A method's return type is a section's canonical read DTO (credit; encourages adoption of the project-blessed read API).",
        ["methodReturnsEntityAcrossSection"] = "A public method returns a type classified as an entity that lives in a different section than the method's owning type.",

        // Dependency use
        ["sameSectionReadService"] = "A constructor dependency on a read-service interface in the same section.",
        ["crossSectionReadInterface"] = "A constructor dependency on a read-service interface owned by a different section.",
        ["crossSectionFullService"] = "A constructor dependency on a full-service interface (or concrete service) owned by a different section.",
        ["crossSectionRepository"] = "A constructor dependency on a repository interface or implementation owned by a different section.",
        ["writeCapableInterfaceUsedReadOnly"] = "A class injects a full-service interface that is paired (by inheritance or by a same-namespace 'Read'-suffixed sibling) with a read-only interface, and every observed invocation on the injected dependency targets a method that also exists on the read interface.",

        // Internal shape
        ["methodParameterOverflow"] = "A public method has more than two parameters; points scale per parameter beyond two.",
        ["booleanParameter"] = "A public method has a boolean parameter (often a control flag that splits the method into hidden branches).",
        ["tupleReturn"] = "A public method's return type is a value tuple.",
        ["optionsBag"] = "A public method takes a single parameter whose type aggregates many public properties (heuristic: name ends in 'Options' with 4+ props, or any type with 6+ public props).",
        ["dashboardAdminPageName"] = "A public method's name contains 'Dashboard', 'ForAdmin', 'ForPage', or 'AdminPage' — caller-shaped naming that often signals a leaked UI concern.",
        ["oneImplementationInterface"] = "A classified interface has exactly one classified class implementing it.",

        // Internal complexity axis
        ["longMethod"] = "A method's nonblank line count exceeds 40; points scale with length (steeper past 100 and 180 lines).",
        ["largeClass"] = "A service/repository/controller class's nonblank line count exceeds 750; points scale with size (steeper past 1500 lines).",
        ["cognitiveComplexity"] = "A method's SonarSource cognitive-complexity score (nesting-weighted branching) exceeds the threshold; points scale with the excess.",
        ["actionDispatcher"] = "A method that mutates state dispatches on a parameter via a switch or if-chain whose arms route to different members with disjoint bodies (low arm cohesion); read methods and arms sharing a base body are exempt.",
        ["genericActionDispatcher"] = "A generic-verb-named mutation method (Apply/Handle/Process/Execute/Create/Save) takes an action/mode enum parameter and its implementation switches on it, delegating arms to different members; flagged on both the interface method and its implementation. State-machine entry points (transition validation/vocabulary) are exempt.",
        ["mutationModeParameter"] = "A public mutation method takes an action/mode enum parameter that selects behavior, even when the body is small and does not delegate; the parameter folds multiple distinct operations behind one signature.",
        ["flagsControlFlow"] = "A method that mutates state branches on a [Flags] enum parameter via HasFlag or bitwise tests; read methods are exempt."
    };

    /// <summary>
    /// Returns descriptions for the subset of rules that actually fired in the given report.
    /// Output ordering matches <paramref name="firedRules"/>'s enumeration order so callers
    /// can sort by score, alphabetic, etc.
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, string>> ForFiredRules(IEnumerable<string> firedRules)
    {
        var result = new List<KeyValuePair<string, string>>();
        foreach (var rule in firedRules)
        {
            if (Descriptions.TryGetValue(rule, out var desc))
                result.Add(new KeyValuePair<string, string>(rule, desc));
        }
        return result;
    }
}
