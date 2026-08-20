using Microsoft.CodeAnalysis;

namespace Reforge.Tests;

/// <summary>
/// Plan B - section-architecture scored rules (crossSectionWriteSurface, missing*,
/// readSurfaceProjectionMethod) + conservationAnchors, exercised end-to-end through
/// SurfaceScoreEngine against the sample solution. Sections are the sample's assemblies
/// (Camp + Camp.Contracts, Lodge, Dorm, Tent, Reporting) - no config maps types into them.
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

    // ---------------- Task 1: weights + glossary + axis ----------------

    [Fact]
    public void Default_HasSectionArchitectureWeights()
    {
        var cfg = SurfaceScoreConfig.Default();
        Assert.Equal(15, cfg.Weight("crossSectionWriteSurface"));
        Assert.Equal(10, cfg.Weight("missingReadSurface"));
        Assert.Equal(10, cfg.Weight("missingWriteSurface"));
        Assert.Equal(10, cfg.Weight("missingPrimaryInfoDto"));
        Assert.Equal(4, cfg.Weight("readSurfaceProjectionMethod"));
    }

    [Fact]
    public void Glossary_HasFactualLinesForNewRules_OnSurfaceAxis()
    {
        foreach (var rule in new[]
        {
            "crossSectionWriteSurface", "missingReadSurface", "missingWriteSurface",
            "missingPrimaryInfoDto", "readSurfaceProjectionMethod"
        })
        {
            Assert.True(SurfaceScoreRuleGlossary.Descriptions.ContainsKey(rule), $"missing glossary: {rule}");
            Assert.False(SurfaceScoreRuleGroups.IsInternalComplexity(rule), $"should be surface axis: {rule}");
        }
    }

    // ---------------- Task 3: readSurfaceProjectionMethod surcharge ----------------

    [Fact]
    public async Task ReadSurfaceProjectionMethod_ChargesProjectionAndPredicateReads()
    {
        var report = await Score();

        Assert.True(report.ByRule.TryGetValue("readSurfaceProjectionMethod", out var pts) && pts > 0);
        var camp = report.Groups["Camp"];
        // Two charged methods (predicate + projection) x weight 4 = 8, doubled to 16 because
        // ICampServiceRead is declared in the satellite SampleSolution.Camp.Contracts assembly.
        // The surcharge is for the shape a read interface publishes, so where it publishes from
        // is exactly what the contracts multiplier prices.
        Assert.Equal(16, camp.ByRule["readSurfaceProjectionMethod"]);
        // surcharge is on the surface axis, not internal complexity
        Assert.False(SurfaceScoreRuleGroups.IsInternalComplexity("readSurfaceProjectionMethod"));
    }

    // ---------------- Task 4: missing* rules (repo-backed gated) ----------------

    [Fact]
    public async Task MissingSurfaceRules_FireOnlyForRepoBackedSections()
    {
        // Each fixture section is its own assembly; repo-backing is read off what it declares.
        var report = await Score();

        Assert.True(report.Groups["Lodge"].ByRule.ContainsKey("missingReadSurface"));
        Assert.True(report.Groups["Dorm"].ByRule.ContainsKey("missingWriteSurface"));
        Assert.True(report.Groups["Tent"].ByRule.ContainsKey("missingPrimaryInfoDto"));

        // Reporting owns no repository and no DbContext: none of the missing* rules
        var reporting = report.Groups["Reporting"];
        Assert.False(reporting.ByRule.ContainsKey("missingReadSurface"));
        Assert.False(reporting.ByRule.ContainsKey("missingWriteSurface"));
        Assert.False(reporting.ByRule.ContainsKey("missingPrimaryInfoDto"));

        // Lodge has LodgeInfo + write but no read; must NOT be charged missingPrimaryInfoDto or missingWriteSurface
        Assert.False(report.Groups["Lodge"].ByRule.ContainsKey("missingPrimaryInfoDto"));
        Assert.False(report.Groups["Lodge"].ByRule.ContainsKey("missingWriteSurface"));
    }

    // ---------------- Task 5: crossSectionWriteSurface + unverified advisory + escape analysis ----------------

    [Fact]
    public async Task CrossSectionWriteSurface_FiresOnCrossSectionReadOnlyConsumer_SuppressesGeneric()
    {
        var report = await Score();

        var reporting = report.Groups["Reporting"];
        Assert.True(reporting.ByRule.ContainsKey("crossSectionWriteSurface"));
        // generic writeCapableInterfaceUsedReadOnly is suppressed for that same dependency (CampReportBuilder)
        Assert.DoesNotContain(reporting.Entries, e => e.Rule == "writeCapableInterfaceUsedReadOnly" && e.Symbol == "CampReportBuilder");
        // the confident penalty is attributed to the cross-section caller
        Assert.Contains(reporting.Entries, e => e.Rule == "crossSectionWriteSurface" && e.Symbol == "CampReportBuilder");
    }

    [Fact]
    public async Task CrossSectionWriteSurfaceUnverified_WhenDependencyEscapes_NoConfidentPenalty()
    {
        var report = await Score();

        // CampDelegator passes the dep onward -> NOT a confident crossSectionWriteSurface; an advisory diagnostic instead.
        var reporting = report.Groups["Reporting"];
        Assert.DoesNotContain(reporting.Entries, e => e.Rule == "crossSectionWriteSurface" && e.Symbol == "CampDelegator");
        Assert.Contains(report.Diagnostics, d => d.Code == "crossSectionWriteSurfaceUnverified" && d.Message.Contains("CampDelegator"));
    }

    // ---------------- Task 6: conservationAnchors emission ----------------

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
        // per-anchor byRule points exist for the read interface (readServiceInterfaceMethod + readSurfaceProjectionMethod)
        Assert.True(read.ByRule.ContainsKey("readServiceInterfaceMethod"));
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
            internalComplexityTotal = now.InternalComplexityTotal,
            byRule = now.ByRule,
            groups = now.Groups.Values.Select(g => new
            {
                name = g.Name,
                total = g.Total,
                surfaceTotal = g.Name == section ? g.SurfaceTotal + 6 : g.SurfaceTotal,
                internalComplexityTotal = g.InternalComplexityTotal,
                byRule = g.Name == section
                    ? g.ByRule.ToDictionary(kv => kv.Key, kv => kv.Key == "readServiceInterfaceMethod" ? kv.Value + 6 : kv.Value)
                    : g.ByRule.ToDictionary(kv => kv.Key, kv => kv.Value)
            }).ToArray(),
            conservationAnchors = anchors,
            helperCandidates = now.HelperCandidates.Select(h => new { display = h.Display, methods = h.Methods }).ToArray()
        };
        return System.Text.Json.JsonSerializer.Serialize(baseline);
    }
}
