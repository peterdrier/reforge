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
        ["exportedReadMethod"] = "A public method on an exported class or interface that reads: no persistence commit in its body or in any corpus method it calls, and it returns data.",
        ["exportedWriteMethod"] = "A public method on an exported class or interface that writes: its body or a corpus method it calls commits persistent state, or it is command-shaped (returns no data). An interface method takes the verdict of its implementations.",
        ["exportedUnusedExternally"] = "A public method on an exported type that no other production assembly names in source; the cheapest fix is to make it internal, which also clears its exported charge.",
        ["repositoryInterfaceMethod"] = "A method declared on a repository interface.",
        ["repositoryImplementationMethod"] = "A public method on a repository implementation class.",
        ["newRepositoryInterface"] = "A repository interface type — each is durable infrastructure surface.",
        ["newRepositoryImplementation"] = "A repository implementation class.",
        ["controllerAction"] = "A public action method on a controller.",
        ["backgroundJob"] = "A background job, hosted service, or worker class.",
        ["duplicateDbSetOwner"] = "A class reads or writes a DbSet that is owned (per config) by a different section.",
        ["publicInputWithHiddenState"] = "A public boundary input/command/request type used as a public method parameter exposes most of its carried state through non-public accessors, so callers and decorators cannot inspect the data crossing the boundary.",
        ["parameterBagInput"] = "An input/command/request type with four or more data members, a direct-assignment constructor, and no behavior, used as the sole or near-sole parameter of a public method — a long method signature folded into an object without adding invariants or domain semantics.",
        ["inlineParameterObjectConstruction"] = "A public method call constructs an input/request/options object inline from a long argument list, moving the argument bundle from the method signature to the construction site.",

        // Dependency use
        ["sameSectionReadService"] = "A constructor dependency on a read-service interface in the same section.",
        ["crossSectionReadInterface"] = "A constructor dependency on a read-service interface owned by a different section.",
        ["crossSectionFullService"] = "A constructor dependency on a full-service interface (or concrete service) owned by a different section.",
        ["crossSectionRepository"] = "A constructor dependency on a repository interface or implementation owned by a different section.",
        ["writeCapableInterfaceUsedReadOnly"] = "A class injects a full-service interface that is paired (by inheritance or by a same-namespace 'Read'-suffixed sibling) with a read-only interface, and every observed invocation on the injected dependency targets a method that also exists on the read interface.",

        // Signature shape
        ["methodParameterOverflow"] = "A public method has more than two parameters; points scale per parameter beyond two.",
        ["booleanParameter"] = "A public method has a boolean parameter (often a control flag that splits the method into hidden branches).",
        ["tupleReturn"] = "A public method's return type is a value tuple.",
        ["optionsBag"] = "A public method takes a single parameter whose type aggregates many public properties (heuristic: name ends in 'Options' with 4+ props, or any type with 6+ public props).",
        ["oneImplementationInterface"] = "A classified interface has exactly one classified class implementing it.",

        // Implementation-shape axis
        ["largeClass"] = "A service/repository/controller class's nonblank line count exceeds 750; points scale with size (steeper past 1500 lines).",
        ["cognitiveComplexity"] = "SonarSource cognitive complexity (nesting-weighted branching) over a method's whole call path — its own body plus every helper in its type that only it calls — exceeds the threshold; points scale with the excess, plus up to one threshold for what single-caller methods it calls in other types kept under it.",
        ["actionDispatcher"] = "A method, read or write, dispatches on a parameter via a switch or if-chain whose arms route to different members with disjoint bodies (low arm cohesion); flagged on both the implementation and any interface method it implements. Surcharges apply when the selector is an action/mode enum, when the name is a generic verb (Apply/Handle/Process/Execute/Create/Save) that hides which operation runs, and when the declaring type is an application service. Arms sharing a base body and state-machine entry points (transition validation/vocabulary) are exempt. A read fires only when two or more arms each call a solution method no other arm calls.",
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
