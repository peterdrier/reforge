using Microsoft.CodeAnalysis;

namespace Reforge.Tests;

/// <summary>
/// One test per verdict, plus the two negative controls that keep the thresholds honest. The fixtures
/// live in <c>SampleSolution.Web/MisplacedFixtures.cs</c> and are sized against the analyzer's
/// constants deliberately: <see cref="MisplacedAnalyzer.MinimumTargetTouches"/> is 3 and the target
/// must out-touch the method's own section by <see cref="MisplacedAnalyzer.DominanceFactor"/>.
/// </summary>
[Collection("SampleSolution")]
public class MisplacedAnalyzerTests
{
    private readonly SampleSolutionFixture _fixture;
    public MisplacedAnalyzerTests(SampleSolutionFixture fixture) => _fixture = fixture;

    private async Task<MisplacedReport> AnalyzeAsync()
    {
        var cfg = SurfaceScoreConfig.Default();
        var dir = LocationHelper.GetSolutionDirectory(_fixture.Solution);
        var classified = (await SolutionClassifier.ClassifyAsync(_fixture.Solution, cfg, dir, CancellationToken.None)).ToList();
        return await MisplacedAnalyzer.AnalyzeAsync(_fixture.Solution, classified, dir, ct: CancellationToken.None);
    }

    private static MisplacedMethod? Find(MisplacedReport report, string method) =>
        report.Findings.FirstOrDefault(f => f.Method.EndsWith("." + method, StringComparison.Ordinal));

    [Fact]
    public async Task Analyze_PlainPipe_IsReportedAsMove()
    {
        var report = await AnalyzeAsync();
        var finding = Find(report, "SummarizeGreetingsForRelocation");

        Assert.NotNull(finding);
        Assert.Equal(MisplacedVerdict.Move, finding.Verdict);
        Assert.Equal("Web", finding.Section);
        Assert.Equal("Services", finding.TargetSection);
        // Three calls into Services and none of its own. The dependency HANDLE (_greetings) is not
        // counted at home: a purely delegating method would otherwise tie 1:1 and never be dominant,
        // which made the move verdict unreachable for the commonest shape it exists to find.
        Assert.Equal(3, finding.TargetBehaviorTouches);
        Assert.Equal(0, finding.OwnTouches);
        Assert.Null(finding.DuplicateOf);
    }

    [Fact]
    public async Task Analyze_PipeNamedForAnExistingTargetMethod_WarnsInsteadOfProposingACopy()
    {
        var report = await AnalyzeAsync();
        var finding = report.Findings.FirstOrDefault(f =>
            f.Method.Contains("DuplicatingGreetingReporter", StringComparison.Ordinal));

        Assert.NotNull(finding);
        Assert.Equal(MisplacedVerdict.MoveWouldDuplicate, finding.Verdict);
        Assert.Equal("Services", finding.TargetSection);
        // The target already declares this name with this exact signature, so the destination could not
        // compile with both — the move cannot be a straight relocation. That is a decisive fact, and
        // separate from whether the two methods DO the same thing, which needs the bodies compared and
        // is not claimed.
        Assert.Contains("GreetingService.GetGreetingAsync(Int32, CancellationToken)", finding.DuplicateOf!, StringComparison.Ordinal);
        Assert.Contains("same signature", finding.DuplicateOf!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Analyze_NullSafeDelegation_IsStillReportedAsMove()
    {
        var report = await AnalyzeAsync();
        var finding = Find(report, "SummarizeNullSafelyAsync");

        Assert.NotNull(finding);
        Assert.Equal(MisplacedVerdict.Move, finding.Verdict);
        Assert.Equal("Services", finding.TargetSection);
        // `_greetings?.Get(...)` puts the receiver under a ConditionalAccessExpression and the invoked
        // name under a member binding past the `?.`, so a member-access-only walk never recognised the
        // receiver. Counted at home it restored the 1:1 tie that makes delegation invisible.
        Assert.Equal(0, finding.OwnTouches);
        Assert.Equal(3, finding.TargetBehaviorTouches);
    }

    [Fact]
    public async Task Analyze_NamesakeOnAnUnrelatedDestinationType_IsNotACollision()
    {
        var report = await AnalyzeAsync();
        var finding = Find(report, "PurgeAsync");

        Assert.NotNull(finding);
        // AuditLogQueryService.PurgeAsync exists in the destination SECTION, but this method leans on
        // GreetingService, which declares no PurgeAsync. A duplicate signature is only prohibited within
        // one containing type, so an assembly-wide name match is not a collision.
        Assert.Equal(MisplacedVerdict.Move, finding.Verdict);
        Assert.Null(finding.DuplicateOf);
    }

    [Fact]
    public async Task Analyze_DefaultInterfaceMethod_IsBlocked()
    {
        var report = await AnalyzeAsync();
        var finding = Find(report, "SummarizeByDefaultAsync");

        Assert.NotNull(finding);
        // The method IS the contract rather than being bound by one, and AllInterfaces excludes the
        // interface a member is declared on, so neither contract branch caught it.
        Assert.Equal(MisplacedVerdict.Blocked, finding.Verdict);
        Assert.Contains("IDefaultPipingReport", finding.BlockedBy!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Analyze_SameParametersDifferentReturnType_IsADecisiveCollision()
    {
        var report = await AnalyzeAsync();
        var finding = report.Findings.FirstOrDefault(f =>
            f.Method.Contains("ReturnTypeClashReporter", StringComparison.Ordinal));

        Assert.NotNull(finding);
        Assert.Equal(MisplacedVerdict.MoveWouldDuplicate, finding.Verdict);
        // C# does not overload on return type, so this cannot compile alongside the existing method.
        // Comparing return types made it a near-miss reported as "different parameter types".
        Assert.Contains("same signature", finding.DuplicateOf!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Analyze_NullForgivingDelegation_IsStillReportedAsMove()
    {
        var report = await AnalyzeAsync();
        var finding = Find(report, "SummarizeForgivinglyAsync");

        Assert.NotNull(finding);
        Assert.Equal(MisplacedVerdict.Move, finding.Verdict);
        Assert.Equal(0, finding.OwnTouches);
    }

    [Fact]
    public async Task Analyze_PipeSharingOnlyANameWithTheTarget_SaysHowTheSignaturesDiffer()
    {
        var report = await AnalyzeAsync();
        var finding = report.Findings.FirstOrDefault(f =>
            f.Method.Contains("NamesakeGreetingReporter", StringComparison.Ordinal));

        Assert.NotNull(finding);
        Assert.Equal(MisplacedVerdict.MoveWouldDuplicate, finding.Verdict);
        // A namesake with a different signature could coexist at the destination, so this is the weaker
        // half of the verdict and has to read as such. Reporting it identically to an exact collision
        // was the imprecision the name-only check could not avoid.
        Assert.DoesNotContain("same signature", finding.DuplicateOf!, StringComparison.Ordinal);
        Assert.Contains("parameter", finding.DuplicateOf!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Analyze_PartialMethodImplementation_IsBlocked()
    {
        var report = await AnalyzeAsync();
        var finding = Find(report, "SummarizePartiallyAsync");

        Assert.NotNull(finding);
        // Only the implementation half is ever measured — the defining half has no body — and it cannot
        // move alone, because C# requires both halves in the same containing type. Neither the override
        // nor the interface branch catches it, so it was reported as a plain move.
        Assert.Equal(MisplacedVerdict.Blocked, finding.Verdict);
        Assert.Contains("partial", finding.BlockedBy!, StringComparison.Ordinal);
        Assert.Contains("SummarizePartiallyAsync", finding.BlockedBy!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Analyze_EquivalentGenericSignatures_IsADecisiveCollision()
    {
        var report = await AnalyzeAsync();
        var finding = report.Findings.FirstOrDefault(f =>
            f.Method.Contains("GenericClashReporter", StringComparison.Ordinal));

        Assert.NotNull(finding);
        Assert.Equal(MisplacedVerdict.MoveWouldDuplicate, finding.Verdict);
        // `Passthrough<T>(T)` and `Passthrough<U>(U)` are one signature to C#. The type parameters are
        // different symbols, so identity comparison rejected the match and then reported the collision
        // as "different parameter types" — the right verdict for the wrong, and false, reason.
        Assert.Contains("same signature", finding.DuplicateOf!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Analyze_ReadsOfAConfiguredDto_IsAMapperNotAMove()
    {
        var report = await AnalyzeAsync();
        var finding = Find(report, "MapConfiguredSummary");

        Assert.NotNull(finding);
        // RelocationSummaryResult is a DTO by config rule and not by shape: it declares a method, which
        // the structural test treats as behavior. Reading its properties is still reading data, and
        // counting those four reads as behavior calls turned a mapper into a move with a destination.
        Assert.Equal(MisplacedVerdict.Mapper, finding.Verdict);
        Assert.Equal(0, finding.TargetBehaviorTouches);
        Assert.Equal(4, finding.TargetDataTouches);
    }

    [Fact]
    public async Task Analyze_CallsOnAConfiguredDto_AreBehaviorNotData()
    {
        var report = await AnalyzeAsync();
        var finding = Find(report, "ShoutSummary");

        Assert.NotNull(finding);
        // A config rule states a type's ROLE and says nothing about its members, so a configured DTO can
        // declare methods. Calling three of them is three behavior calls; classifying every touch on such
        // a type as a data read reported this as a mapper.
        Assert.Equal(MisplacedVerdict.Move, finding.Verdict);
        Assert.Equal(3, finding.TargetBehaviorTouches);
        Assert.Equal(0, finding.TargetDataTouches);
    }

    [Fact]
    public async Task Analyze_WorkInsideALocalFunction_IsChargedToTheEnclosingMethod()
    {
        var report = await AnalyzeAsync();
        var finding = Find(report, "SummarizeViaLocalFunctionAsync");

        Assert.NotNull(finding);
        // Deliberate, and pinned here so it is not "fixed" by accident. A local function cannot be
        // relocated on its own: it moves with the method that declares it. Its calls are therefore part
        // of what moving the enclosing method would move, and excluding them would report a method whose
        // entire body delegates to a local helper as touching nothing at all.
        Assert.Equal(MisplacedVerdict.Move, finding.Verdict);
        Assert.Equal("Services", finding.TargetSection);
        Assert.Equal(3, finding.TargetBehaviorTouches);
    }

    [Fact]
    public async Task Analyze_NamesakeDifferingOnlyInRefKind_IsADecisiveCollision()
    {
        var report = await AnalyzeAsync();
        var finding = report.Findings.FirstOrDefault(f =>
            f.Method.Contains("RefKindClashReporter", StringComparison.Ordinal));

        Assert.NotNull(finding);
        Assert.Equal(MisplacedVerdict.MoveWouldDuplicate, finding.Verdict);
        // C# refuses two declarations differing only in ref/out/in (CS0663), so `TryPassthrough(ref string)`
        // cannot join a type declaring `TryPassthrough(out string)`. Comparing the RefKind enum exactly
        // rejected the match and then reported it as "different parameter types".
        Assert.Contains("same signature", finding.DuplicateOf!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Analyze_InheritedPropertiesOfAConfiguredDto_AreData()
    {
        var report = await AnalyzeAsync();
        var finding = Find(report, "MapInheritedSummary");

        Assert.NotNull(finding);
        // A member's containing type is where it is DECLARED, not what the caller holds. Three of these
        // four reads resolve to BehaviorfulRowBase, which no config rule names, so they counted as
        // behavior calls on the base and made a mapper into a move.
        Assert.Equal(MisplacedVerdict.Mapper, finding.Verdict);
        Assert.Equal(0, finding.TargetBehaviorTouches);
        Assert.Equal(4, finding.TargetDataTouches);
    }

    [Fact]
    public async Task Analyze_ContractSuppliedToADerivedType_IsBlocked()
    {
        var report = await AnalyzeAsync();
        var finding = Find(report, "RenderInheritedAsync");

        Assert.NotNull(finding);
        // InheritedContractReporter : Base, IInheritedContractReport is served by the inherited Base
        // method. Base.AllInterfaces is empty, so asked from the declaring type there is no contract to
        // find — while moving the method would leave the derived type without an implementation.
        Assert.Equal(MisplacedVerdict.Blocked, finding.Verdict);
        Assert.Contains("IInheritedContractReport", finding.BlockedBy!, StringComparison.Ordinal);
        Assert.Contains("InheritedContractReporter", finding.BlockedBy!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Analyze_Move_NamesTheDestinationType()
    {
        var report = await AnalyzeAsync();
        var finding = Find(report, "SummarizeGreetingsForRelocation");

        Assert.NotNull(finding);
        // A section is not a place a method goes. The analyzer already picks the concrete type the method
        // leans on hardest — it has to, for the collision check to be sayable — and discarding it meant
        // the actionable verdict named only an assembly unless a namesake happened to exist.
        Assert.Equal(MisplacedVerdict.Move, finding.Verdict);
        Assert.EndsWith(".GreetingService", finding.DestinationType!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Analyze_ConstructingAnotherSectionsTypes_CountsAsTouches()
    {
        var report = await AnalyzeAsync();
        var finding = Find(report, "BuildWorkItems");

        Assert.NotNull(finding);
        // `new T(...)` carries its constructor on the creation expression; the type name inside binds to
        // the type. A method whose foreign work is all construction therefore measured as touching
        // nothing, while one calling the same section once measured as touching it.
        Assert.Equal("Services", finding.TargetSection);
        Assert.Equal(3, finding.TargetBehaviorTouches);
        Assert.Equal(0, finding.OwnTouches);
    }

    [Fact]
    public async Task Analyze_ContractThroughAConstructedGenericBase_IsBlocked()
    {
        var report = await AnalyzeAsync();
        var finding = Find(report, "RenderGenericAsync");

        Assert.NotNull(finding);
        // The interface map resolves to the SUBSTITUTED Base<int>.RenderGenericAsync(int), while the
        // method measured from syntax is Base<T>.RenderGenericAsync(T). Keyed as substituted, the index
        // added for inherited contracts never matched the very method it exists to pin.
        Assert.Equal(MisplacedVerdict.Blocked, finding.Verdict);
        Assert.Contains("IGenericContractReport", finding.BlockedBy!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Analyze_EnumMemberReads_AreDataAndProposeNoDestinationType()
    {
        var report = await AnalyzeAsync();
        var finding = Find(report, "SizeToSquareMetres");

        Assert.NotNull(finding);
        // An enum has no behavior to call. Counted as calls, enum members made the evidence say
        // "N call(s) into Services" where nothing is called, and let the enum win the destination
        // contest — proposing a move onto a type that cannot declare a method. Found on Humans, where
        // five of twelve destinations were enums once the destination became visible.
        Assert.Equal(MisplacedVerdict.Mapper, finding.Verdict);
        Assert.Equal(0, finding.TargetBehaviorTouches);
        // Three named patterns; the discard arm names no member.
        Assert.Equal(3, finding.TargetDataTouches);
        Assert.Null(finding.DestinationType);
    }

    [Fact]
    public async Task Analyze_TouchesThroughAnIndexer_AreMeasured()
    {
        var report = await AnalyzeAsync();
        var finding = Find(report, "SummarizeBySlot");

        Assert.NotNull(finding);
        // An indexer hangs off the element-access expression, not off any identifier, so three reads
        // through one measured as nothing and the method vanished from the report.
        Assert.Equal("Services", finding.TargetSection);
        Assert.Equal(3, finding.TargetBehaviorTouches);
        Assert.Equal(0, finding.OwnTouches);
    }

    [Fact]
    public async Task Analyze_ContractOnOneRefOverload_DoesNotBlockTheOther()
    {
        var report = await AnalyzeAsync();
        var findings = report.Findings
            .Where(f => f.Method.EndsWith(".HandleRefOverload", StringComparison.Ordinal))
            .ToList();

        Assert.Equal(2, findings.Count);
        // `M(ref int)` and `M(int)` are two distinct methods, so only the one the interface map resolves
        // to is pinned. Keyed without the ref kind, both collided on one entry and the unconstrained
        // overload was reported as blocked — the opposite failure to the one the index was added to fix.
        Assert.Contains(findings, f => f.Verdict == MisplacedVerdict.Blocked);
        Assert.Contains(findings, f => f.Verdict == MisplacedVerdict.Move);
    }

    [Fact]
    public async Task Analyze_DestinationType_IsNamespaceQualified()
    {
        var report = await AnalyzeAsync();
        var finding = Find(report, "SummarizeGreetingsForRelocation");

        Assert.NotNull(finding);
        // A section can hold two types of one name in different namespaces, and nothing else in the
        // output says which was meant — no destination file or namespace is reported anywhere.
        Assert.Equal("SampleSolution.Services.GreetingService", finding.DestinationType);
        // Assembled by hand from namespace and simple name, a nested type loses its containing type:
        // OuterA.SharedName and OuterB.SharedName would both render as SampleSolution.Services.SharedName.
        Assert.All(
            report.Findings.Where(f => f.DestinationType?.EndsWith(".SharedName", StringComparison.Ordinal) == true),
            f => Assert.Contains("Outer", f.DestinationType!, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Analyze_IndexerHeldInAField_TreatsTheReceiverAsAConduit()
    {
        var report = await AnalyzeAsync();
        var finding = Find(report, "SummarizeHeldSlots");

        Assert.NotNull(finding);
        // `_table[0]` reaches another section through an indexer, so the receiver is a conduit exactly as
        // in `_dep.Method()`. Unrecognised, three reads scored 3 own against 3 target and tied — the
        // shape the conduit rule exists to break. The earlier indexer fixture took its table as a
        // parameter and so never exercised this path.
        Assert.Equal(0, finding.OwnTouches);
        Assert.Equal(3, finding.TargetBehaviorTouches);
    }

    [Fact]
    public async Task Analyze_NullSafeIndexerReads_AreMeasured()
    {
        var report = await AnalyzeAsync();
        var finding = Find(report, "SummarizeNullSafeSlots");

        Assert.NotNull(finding);
        // `table?[0]` hangs the indexer off an ElementBindingExpression, a different node type from the
        // ElementAccessExpression of `table[0]` — the same split that once hid `?.` calls.
        Assert.Equal("Services", finding.TargetSection);
        Assert.Equal(3, finding.TargetBehaviorTouches);
    }

    [Fact]
    public async Task Analyze_ContractOnAPrivateDerivedType_IsStillBlocked()
    {
        var report = await AnalyzeAsync();
        var finding = Find(report, "DescribePrivately");

        Assert.NotNull(finding);
        // The classifier drops effectively private types, so an index built from that list never saw a
        // private `Derived : Base, IFoo`. The compiler does not care who can see the implementer: moving
        // the base method breaks the build either way.
        Assert.Equal(MisplacedVerdict.Blocked, finding.Verdict);
        Assert.Contains("IPrivatelyImplementedContract", finding.BlockedBy!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Analyze_MethodSpanningThreeSections_IsAnOrchestratorNotAMove()
    {
        var report = await AnalyzeAsync();
        var finding = Find(report, "BuildDashboardAsync");

        Assert.NotNull(finding);
        Assert.Equal(MisplacedVerdict.Orchestrator, finding.Verdict);
        // No destination: naming one would be picking a winner among sections that are all needed.
        Assert.Null(finding.TargetSection);
        Assert.True(finding.SectionsTouched.Count >= MisplacedAnalyzer.OrchestratorFanOut);
    }

    [Fact]
    public async Task Analyze_DataOnlyReader_IsAMapperNotAMove()
    {
        var report = await AnalyzeAsync();
        var finding = Find(report, "MapToRow");

        Assert.NotNull(finding);
        Assert.Equal(MisplacedVerdict.Mapper, finding.Verdict);
        // Every touch is a property of a data carrier and none is behavior. Mapping code belongs to
        // whoever needs the mapped shape, so the touch count says nothing about where it should live.
        // The two counts are separate totals, not a whole and a part: data reads are never behavior calls.
        Assert.Equal(0, finding.TargetBehaviorTouches);
        Assert.Equal(4, finding.TargetDataTouches);
    }

    [Fact]
    public async Task Analyze_ContractBoundPipe_IsBlockedNotAMove()
    {
        var report = await AnalyzeAsync();
        var finding = Find(report, "RenderAsync");

        Assert.NotNull(finding);
        Assert.Equal(MisplacedVerdict.Blocked, finding.Verdict);
        // The finding is still true; the fix is bigger than moving a file, because the interface
        // would have to move with it.
        Assert.Contains("IRelocationReport", finding.BlockedBy!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Analyze_TwoCallsIntoAnotherSection_IsNotReported()
    {
        var report = await AnalyzeAsync();

        // Two calls into a dependency is what most delegating code in any solution looks like. If this
        // is reported, the command's output is every method that uses a dependency twice.
        Assert.Null(Find(report, "GetOneAsync"));
    }

    [Fact]
    public async Task Analyze_MethodWorkingOnBothSectionsEqually_IsNotReported()
    {
        var report = await AnalyzeAsync();

        // Over the touch threshold but not dominant: three touches out, three of its own. A count
        // alone would report it; the dominance factor is what does not.
        Assert.Null(Find(report, "BlendAsync"));
    }

    [Fact]
    public async Task Analyze_BuildsSectionGraphFromEveryMethod_NotJustFindings()
    {
        var report = await AnalyzeAsync();

        // Fan-in/fan-out must come from the whole measured population. Computing it from the reported
        // findings alone made every section look like a leaf, which silently disabled the foundation
        // exemption — a section everything depends on and which depends on nothing is not misplaced.
        Assert.Contains("Core", report.Sections.Keys);
        Assert.True(report.Sections["Core"].FanIn > 0);
        Assert.All(report.Sections.Values, p => Assert.True(p.FanIn >= 0 && p.FanOut >= 0));
    }

    [Fact]
    public async Task Analyze_NullSafeHeldIndexer_TreatsTheReceiverAsAConduit()
    {
        var report = await AnalyzeAsync();
        var finding = Find(report, "SummarizeNullSafeHeldSlots");

        // `_table?[0]` puts the reached indexer on an ElementBindingExpression, so a conduit walk
        // looking only for member bindings counted the receiver as own state three times. The 3:3 tie
        // that produced suppressed the finding entirely — the same delegation `_table[0]` reports.
        Assert.NotNull(finding);
        Assert.Equal(0, finding.OwnTouches);
        Assert.Equal(3, finding.TargetBehaviorTouches);
    }

    [Fact]
    public async Task Analyze_MethodUsingItsContainingTypesTypeParameter_IsBlocked()
    {
        var report = await AnalyzeAsync();
        var finding = Find(report, "DescribeWithTypeParameter");

        // A dominant pipe, and still not movable: `T` is declared on `GenericSourceReporter<T>`, so no
        // destination can declare this method as written. Reported as a plain move it would understate
        // the work by a generic redesign.
        Assert.NotNull(finding);
        Assert.Equal(MisplacedVerdict.Blocked, finding.Verdict);
        Assert.Equal(3, finding.TargetBehaviorTouches);
        Assert.NotNull(finding.BlockedBy);
        Assert.Contains("type parameter T", finding.BlockedBy);
    }

    [Fact]
    public async Task Analyze_InheritedIndexerReadsOfAConfiguredDto_AreData()
    {
        var report = await AnalyzeAsync();

        // An indexer carries its receiver on the element access itself, not on a parent member access,
        // so a receiver lookup written only for the latter judged these reads by the unconfigured base
        // that declares the indexer — and three data reads read as three behavior calls, which is a
        // move rather than a mapper. Both spellings of the read reach the receiver the same way.
        foreach (var method in new[] { "SummarizeInheritedIndexedRow", "SummarizeNullSafeInheritedIndexedRow" })
        {
            var finding = Find(report, method);
            Assert.NotNull(finding);
            Assert.Equal(MisplacedVerdict.Mapper, finding.Verdict);
            Assert.Equal(3, finding.TargetDataTouches);
            Assert.Equal(0, finding.TargetBehaviorTouches);
        }
    }

    [Fact]
    public async Task Analyze_CastReceivers_AreStillConduits()
    {
        var report = await AnalyzeAsync();

        // `((GreetingService)_greetings).Method()` reaches the other section exactly as `_greetings`
        // alone would. Stopping the wrapper walk at the cast scored the receiver as own state three
        // times, tying 3:3 and suppressing the pipe. `as` is the same conversion as an operator.
        foreach (var method in new[] { "SummarizeThroughCast", "SummarizeThroughAsCast" })
        {
            var finding = Find(report, method);
            Assert.NotNull(finding);
            Assert.Equal(0, finding.OwnTouches);
            Assert.Equal(3, finding.TargetBehaviorTouches);
        }
    }

    [Fact]
    public async Task Analyze_UserDefinedOperatorUses_AreMeasured()
    {
        var report = await AnalyzeAsync();
        var finding = Find(report, "SumSlotWeights");

        // `a + b` carries `op_Addition` on the binary expression, the same way `new T()` carries its
        // constructor and `t[0]` its indexer. Measured by name only, a method whose entire body works
        // on another section through operators touched nothing at all and never appeared.
        Assert.NotNull(finding);
        Assert.Equal("Services", finding.TargetSection);
        Assert.Equal(4, finding.TargetBehaviorTouches);
        Assert.Equal(0, finding.OwnTouches);
    }

    [Fact]
    public async Task Analyze_MethodSpanningTwoSections_IsAnOrchestrator()
    {
        var report = await AnalyzeAsync();
        var finding = Find(report, "SummarizeTwoSectionsAsync");

        // Two is the smallest fan-out at which the orchestrator argument holds: neither section could
        // host this without leaving the other reached from the wrong side. This population used to be
        // a separate verdict that made the same claim in weaker words.
        Assert.NotNull(finding);
        Assert.Equal(MisplacedVerdict.Orchestrator, finding.Verdict);
        Assert.Null(finding.TargetSection);
        Assert.Equal(2, finding.SectionsTouched.Count);
        // The per-section split is the evidence that separates an even spread from a lean.
        Assert.Contains(":2", finding.Evidence);
    }

    [Fact]
    public async Task Analyze_AwaitedReceivers_AreStillConduits()
    {
        var report = await AnalyzeAsync();
        var finding = Find(report, "SummarizeThroughAwaitAsync");

        // `(await _greetings).Method()` reaches the other section exactly as `_greetings.Method()`
        // would. Stopping the wrapper walk at the await scored the held task as own state three times,
        // tying 3:3 and suppressing a pipe whose body is nothing but delegation.
        Assert.NotNull(finding);
        Assert.Equal(0, finding.OwnTouches);
        Assert.Equal(3, finding.TargetBehaviorTouches);
    }
}
