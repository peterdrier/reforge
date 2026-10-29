using Microsoft.CodeAnalysis;

namespace Reforge.Tests;

/// <summary>
/// Section architecture through SurfaceScoreEngine against the sample solution: the
/// write-capable-use rule, the escape advisory, conservationAnchors, and the conservation gate.
/// Sections are the sample's assemblies (Camp + Camp.Contracts, Lodge, Dorm, Tent, Reporting) -
/// no config maps types into them.
/// </summary>
[Collection("SampleSolution")]
public class SectionArchitectureTests
{
    private readonly SampleSolutionFixture _fixture;
    public SectionArchitectureTests(SampleSolutionFixture fixture) => _fixture = fixture;

    private string Dir => LocationHelper.GetSolutionDirectory(_fixture.Solution);

    private async Task<ScoreReport> Score(SurfaceScoreConfig? cfg = null)
    {
        var engine = new SurfaceScoreEngine(cfg ?? SurfaceScoreConfig.Default(), Dir);
        return await engine.ScoreAsync(_fixture.Solution, CancellationToken.None);
    }

    // ---------------- Cross-section read-only use of a write-capable interface ----------------

    [Fact]
    public async Task WriteCapableUsedReadOnly_ChargesTheCrossSectionReadOnlyConsumer()
    {
        var report = await Score();

        // CampReportBuilder injects the Camp full interface from another section and only reads.
        // One rule prices that, whichever side of a section boundary the dependency comes from;
        // the boundary itself is priced by crossSectionFullService.
        var reporting = report.Groups["Reporting"];
        Assert.Contains(reporting.Entries, e => e.Rule == "writeCapableInterfaceUsedReadOnly" && e.Symbol == "CampReportBuilder");
    }

    [Fact]
    public async Task WriteSurfaceUnverified_WhenDependencyEscapes_IsAdvisoryOnly()
    {
        var report = await Score();

        // CampDelegator passes the dep onward, so its calls are not provably read-only: no charge,
        // an advisory diagnostic instead.
        var reporting = report.Groups["Reporting"];
        Assert.DoesNotContain(reporting.Entries, e => e.Rule == "writeCapableInterfaceUsedReadOnly" && e.Symbol == "CampDelegator");
        Assert.Contains(report.Diagnostics, d => d.Code == "writeSurfaceUseUnverified" && d.Message.Contains("CampDelegator"));
    }

    // ---------------- conservationAnchors emission ----------------

    [Fact]
    public async Task ConservationAnchors_EmittedFqKeyedWithRecursivePaths()
    {
        var report = await Score();

        var primary = report.ConservationAnchors.Single(a => a.Key.EndsWith("CampInfo") && a.Role == "primaryInfoDto");
        Assert.Equal("Camp", primary.Section);
        Assert.Contains("CampInfo.Seasons[].Members[].UserId", primary.Paths);
        // interface anchor carries {name, returns}
        var read = report.ConservationAnchors.Single(a => a.Key.EndsWith("ICampServiceRead") && a.Role == "readServiceInterface");
        Assert.Contains(read.Methods, m => m.Name == "GetByIdAsync");
        // per-anchor byRule points exist for the read interface
        Assert.True(read.ByRule.ContainsKey("exportedReadMethod"));
    }

    [Fact]
    public async Task ConservationAnchors_AreReportLevel_IndependentOfTopCap()
    {
        // The engine emits conservationAnchors at the report level; no command --top/--top-symbols
        // cap can suppress them. Assert the list is populated straight off the engine.
        var report = await Score();

        Assert.NotEmpty(report.ConservationAnchors);
        Assert.Contains(report.ConservationAnchors, a => a.Role == "readServiceInterface");
        Assert.Contains(report.ConservationAnchors, a => a.Role == "primaryInfoDto");
    }

    // ---------------- Plan C: conservation gate end-to-end through real anchors ----------------

    [Fact]
    public async Task ConservationGate_EndToEnd_ExistingFactConsolidation()
    {
        var now = await Score();

        // Baseline = the current real anchors + an EXTRA removed read method "GetMembersAsync"
        // whose fact "Members" is already on CampInfo.Seasons[].Members[...] in both the baseline
        // and current inventories -> existingDtoFact -> canonical-consolidation.
        var path = Path.GetTempFileName();
        File.WriteAllText(path, BuildBaselineJson(now, "Camp", ("GetMembersAsync", "List<CampMemberInfo>")));
        try
        {
            var cmp = SurfaceScoreBaseline.Compare(now, path);
            var v = cmp.ConservationVerdicts.Single(x => x.Section == "Camp");
            Assert.Equal("canonical-consolidation", v.Kind);
            Assert.True(v.Improvement);
            Assert.Contains(v.Methods, m => m.RemovedMethod == "GetMembersAsync" && m.CoverageKind == "existingDtoFact");
        }
        finally { File.Delete(path); }
    }

    /// <summary>
    /// Serializes a real report into a baseline JSON, appending one extra "removed" read method to
    /// the section's read-interface anchor (and bumping its read-surface points) so the gate sees a drop.
    /// </summary>
    private static string BuildBaselineJson(ScoreReport now, string section, (string name, string returns) extraMethod)
    {
        var anchors = now.ConservationAnchors.Select(a =>
        {
            var methods = a.Methods.Select(m => new { name = m.Name, returns = m.Returns }).ToList();
            if (a.Section == section && a.Role == "readServiceInterface")
                methods.Add(new { name = extraMethod.name, returns = extraMethod.returns });
            return new { key = a.Key, section = a.Section, role = a.Role, paths = a.Paths, methods };
        }).ToArray();

        var baseline = new
        {
            total = now.Total,
            surfaceTotal = now.SurfaceTotal,
            implementationShapeTotal = now.ImplementationShapeTotal,
            byRule = now.ByRule,
            groups = now.Groups.Values.Select(g => new
            {
                name = g.Name,
                total = g.Total,
                surfaceTotal = g.Name == section ? g.SurfaceTotal + 6 : g.SurfaceTotal,
                implementationShapeTotal = g.ImplementationShapeTotal,
                byRule = g.Name == section
                    ? g.ByRule.ToDictionary(kv => kv.Key, kv => kv.Key == "exportedReadMethod" ? kv.Value + 6 : kv.Value)
                    : g.ByRule.ToDictionary(kv => kv.Key, kv => kv.Value)
            }).ToArray(),
            conservationAnchors = anchors,
            helperCandidates = now.HelperCandidates.Select(h => new { display = h.Display, methods = h.Methods }).ToArray()
        };
        return System.Text.Json.JsonSerializer.Serialize(baseline);
    }
}
