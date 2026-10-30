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
        ["crossSectionConcreteService"] = "A class depends, through its constructor, on a concrete service class from another section rather than on an interface it publishes. Dearer than crossSectionFullService: the consumer binds the whole implementation.",
        ["crossSectionFullService"] = "A constructor dependency on a full-service interface owned by a different section.",
        ["crossSectionRepository"] = "A constructor dependency on a repository interface or implementation owned by a different section.",
        ["writeCapableInterfaceUsedReadOnly"] = "A class injects a full-service interface that is paired (by inheritance or by a same-namespace 'Read'-suffixed sibling) with a read-only interface, and every observed invocation on the injected dependency targets a method that also exists on the read interface.",

        // Signature shape
        ["methodParameterOverflow"] = "A public method has more than two parameters; points scale per parameter beyond two.",
        ["internalWiderThanUse"] = "A method visible to its whole assembly (internal, or public on a type the assembly does not export) that nothing outside its own type names; it could be private. Overridden members, overrides, interface implementations and attributed methods are exempt.",
        ["protectedWiderThanUse"] = "A protected method that no derived type names or overrides; it could be private. Cheaper than internalWiderThanUse, which is cheaper than public surface.",
        ["booleanParameter"] = "A public method has a boolean parameter (often a control flag that splits the method into hidden branches).",
        ["tupleReturn"] = "A public method's return type is a value tuple.",
        ["optionsBag"] = "A public method takes a single parameter whose type aggregates many public properties (heuristic: name ends in 'Options' with 4+ props, or any type with 6+ public props).",
        ["oneImplementationInterface"] = "A classified interface has exactly one classified class implementing it.",

        // Implementation-shape axis
        ["largeClass"] = "A service/repository/controller class's nonblank line count exceeds 750; points scale with size (steeper past 1500 lines).",
        ["methodComplexity"] = "Every declaration that carries code (method, constructor, operator, computed property or accessor, delegate-valued field), whatever its accessibility, is charged for its own SonarSource cognitive complexity: linearly up to a threshold (default 10), plus a tax on the excess that grows faster than it, so a method far past the threshold costs more than the same branching held in several methods near it.",
        ["methodCount"] = "A flat charge per declaration that carries code, local functions included, whatever its accessibility; reported once per type. Below the complexity threshold it is the only charge that depends on how many declarations hold the logic.",
        ["actionDispatcher"] = "A method, read or write, branches on a selector parameter (an enum, a bool, or one named or typed like a mode) with a switch or if-chain whose arms are operations of their own: each calls a solution method no other arm calls, touches state of the type no other arm touches, or writes state. Arms are judged by what they do, inline or delegated alike. Each arm past the first pays the surface price of the method it replaces, each inline arm pays the flat charge its own method would (less the nesting it carries only for sitting in the branch), plus a surcharge of 20, more for an action/mode enum, a generic verb (Apply/Handle/Process/Execute/Create/Save) and an application service. Arms that only project shared work, and state-machine entry points, are exempt. Flagged on both the implementation and any interface method it implements.",
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
