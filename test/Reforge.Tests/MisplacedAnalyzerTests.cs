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
}
