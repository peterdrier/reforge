using Microsoft.CodeAnalysis;

namespace Reforge.Tests;

/// <summary>
/// Issue #13 — a section is an assembly, so its surface is what the assembly exports. These tests
/// pin the split: rules charging for a <b>declaration's</b> published shape skip anything not
/// effectively public, while rules charging for a <b>use</b> (cross-section coupling, DbSet
/// ownership, DI registration) keep firing regardless — marking a consumer internal does not
/// remove the assembly reference. Fixtures live in SampleSolution.Reporting/EncapsulationFixtures.cs.
/// </summary>
[Collection("SampleSolution")]
public class EffectiveAccessibilityTests
{
    private readonly SampleSolutionFixture _fixture;
    public EffectiveAccessibilityTests(SampleSolutionFixture fixture) => _fixture = fixture;

    private string Dir => LocationHelper.GetSolutionDirectory(_fixture.Solution);

    private async Task<ScoreReport> Score()
    {
        var engine = new SurfaceScoreEngine(SurfaceScoreConfig.Default(), Dir);
        return await engine.ScoreAsync(_fixture.Solution, CancellationToken.None);
    }

    private async Task<IReadOnlyList<ClassifiedType>> Classify() =>
        await SolutionClassifier.ClassifyAsync(
            _fixture.Solution, SurfaceScoreConfig.Default(), Dir, CancellationToken.None);

    // ---------------- The primitive ----------------

    [Fact]
    public async Task IsExported_IsFalseForInternalTypes_AndForPublicNestedInsideThem()
    {
        var classified = await Classify();
        ClassifiedType Find(string name) => classified.First(c => c.Type.Name == name);

        Assert.True(Find("CampReportBuilder").IsExported);
        Assert.False(Find("InternalReportService").IsExported);
        Assert.False(Find("InternalReportInfo").IsExported);

        // public, but nested in an internal type — the case a declared-accessibility check misses.
        var payload = Find("PayloadInfo");
        Assert.Equal(Accessibility.Public, payload.Type.DeclaredAccessibility);
        Assert.False(payload.IsExported);
    }

    [Fact]
    public async Task InternalTypes_StayInTheCorpus_SoSizingRulesStillSeeThem()
    {
        var classified = await Classify();
        Assert.Contains(classified, c => c.Type.Name == "InternalReportService");
    }

    // ---------------- Declaration rules: gated ----------------

    [Fact]
    public async Task DurableSurfaceRules_SkipInternalDeclarations()
    {
        var reporting = (await Score()).Groups["Reporting"];

        // applicationServiceMethod / methodParameterOverflow / booleanParameter all charge for the
        // shape of a signature nobody outside the assembly can call. (crossSectionFullService on
        // the same class is a use, not a declaration — asserted separately below.)
        Assert.DoesNotContain(reporting.Entries,
            e => e.Symbol == "InternalReportService" && e.Rule != "crossSectionFullService");
        Assert.DoesNotContain(reporting.Entries, e => e.Symbol == "RenderAsync");
        Assert.DoesNotContain(reporting.Entries, e => e.Symbol == "InternalReportInfo");
        Assert.DoesNotContain(reporting.Entries, e => e.Symbol == "PayloadInfo");

        // The exported consumer in the same section still scores normally — the gate is per
        // declaration, not a blanket exemption for the section.
        Assert.Contains(reporting.Entries, e => e.Symbol == "CampReportBuilder");
    }

    [Fact]
    public async Task OneImplementationInterface_SkipsInternalInterfaces()
    {
        var reporting = (await Score()).Groups["Reporting"];

        Assert.DoesNotContain(reporting.Entries,
            e => e.Rule == "oneImplementationInterface" && e.Symbol == "IInternalReportSink");
        Assert.Contains(reporting.Entries,
            e => e.Rule == "oneImplementationInterface" && e.Symbol == "IBookingOrchestrator");
    }

    // ---------------- Gate is per member, not just per type ----------------

    [Fact]
    public async Task BoundaryInput_ReachableOnlyThroughNonPublicInterfaceMethod_DoesNotScore()
    {
        var reporting = (await Score()).Groups["Reporting"];

        // IReportExporter is exported, but HiddenExportInput is only ever a parameter of its
        // C# 8 `private static` member — no external consumer can pass one.
        Assert.DoesNotContain(reporting.Entries, e => e.Symbol == "HiddenExportInput");
    }

    [Fact]
    public async Task ConservationAnchors_ExcludeInternalInterfaces()
    {
        var report = await Score();

        // An internal read interface scores nothing, so anchoring it would let a later deletion of
        // one of its methods read as capability evaporation against a baseline.
        Assert.DoesNotContain(report.ConservationAnchors,
            a => a.Key.Contains("IInternalDigestServiceRead", StringComparison.Ordinal));

        // Exported interfaces are still anchored.
        Assert.Contains(report.ConservationAnchors,
            a => a.Key.Contains("ICampServiceRead", StringComparison.Ordinal));
    }

    // ---------------- Use rules: NOT gated ----------------

    [Fact]
    public async Task CrossSectionCoupling_StillScores_WhenTheConsumerIsInternal()
    {
        var reporting = (await Score()).Groups["Reporting"];

        // InternalReportService injects Camp's full interface. Nothing about marking the consumer
        // internal removes that assembly reference, so the penalty must survive — otherwise
        // "make it internal" becomes a free way to shed coupling debt.
        Assert.Contains(reporting.Entries,
            e => e.Rule == "crossSectionFullService" && e.Symbol == "InternalReportService");
    }
}
