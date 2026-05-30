using System.Text.Json;

namespace Reforge.Tests;

/// <summary>
/// Plan C - the baseline conservation gate. Synthetic current reports + hand-authored baseline
/// JSON exercise each branch of the decision tree (helper-first, coverage check, the three
/// verdict kinds) deterministically.
/// </summary>
public class SurfaceScoreBaselineTests
{
    [Fact]
    public void Compare_BaselineMissingAnchors_AddsPrecisionDiagnostic()
    {
        var baseline = """
            { "total": 10, "surfaceTotal": 10, "internalComplexityTotal": 0, "byRule": {}, "groups": [] }
            """;
        var path = Path.GetTempFileName();
        File.WriteAllText(path, baseline);
        try
        {
            var cmp = SurfaceScoreBaseline.Compare(new ScoreReport(), path);
            Assert.True(cmp.BaselineAnchorsMissing);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Gate_ExistingDtoFact_IsCanonicalConsolidation()
    {
        var (now, path) = Setup("Camp",
            nowAnchors: new() { PrimaryAnchor("Camp", "CampInfo.Seasons[].Members[].UserId"), ReadIface("Camp") },
            nowHelpers: new(),
            baselineMethods: new() { ("GetMembersAsync", "List<CampMemberInfo>") },
            baselinePrimaryPaths: new() { "CampInfo.Seasons[].Members[].UserId" },
            baselineReadSurface: 12, nowReadSurface: 6, baselineHelpers: new());
        try
        {
            var cmp = SurfaceScoreBaseline.Compare(now, path);
            var v = cmp.ConservationVerdicts.Single(x => x.Section == "Camp");
            Assert.Equal("canonical-consolidation", v.Kind);
            Assert.True(v.Improvement);
            Assert.Contains(v.Methods, m => m.RemovedMethod == "GetMembersAsync" && m.CoverageKind == "existingDtoFact" && m.TargetDto == "primaryInfoDto");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Gate_NewHelperAbsorbsRemoved_BeatsAmbiguity()
    {
        // Removed method absorbed by a NEW helper, with NO DTO coverage -> helperExtractionNoConceptDeleted,
        // NOT canonical-consolidation. The core gaming-hole regression guard.
        var (now, path) = Setup("Camp",
            nowAnchors: new() { PrimaryAnchor("Camp", "CampInfo.Id"), ReadIface("Camp") },
            nowHelpers: new() { new HelperCandidate("Foo.CampReadModelProjection", new[] { "BuildCampDetail" }) },
            baselineMethods: new() { ("BuildCampDetailAsync", "CampDetailData") },
            baselinePrimaryPaths: new() { "CampInfo.Id" },
            baselineReadSurface: 10, nowReadSurface: 0, baselineHelpers: new());
        try
        {
            var cmp = SurfaceScoreBaseline.Compare(now, path);
            var v = cmp.ConservationVerdicts.Single(x => x.Section == "Camp");
            Assert.Equal("helperExtractionNoConceptDeleted", v.Kind);
            Assert.False(v.Improvement);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Gate_UncoveredCoreRead_IsCapabilityEvaporation()
    {
        var (now, path) = Setup("Camp",
            nowAnchors: new() { PrimaryAnchor("Camp", "CampInfo.Id"), ReadIface("Camp") },
            nowHelpers: new(),
            baselineMethods: new() { ("GetBySlugAsync", "CampInfo") },
            baselinePrimaryPaths: new() { "CampInfo.Id" },
            baselineReadSurface: 6, nowReadSurface: 0, baselineHelpers: new());
        try
        {
            var cmp = SurfaceScoreBaseline.Compare(now, path);
            var v = cmp.ConservationVerdicts.Single(x => x.Section == "Camp");
            Assert.Equal("capability-evaporation", v.Kind);
            Assert.False(v.Improvement);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Gate_SettingsFact_TargetsSettingsDto()
    {
        var nowAnchors = new List<ConservationAnchor>
        {
            PrimaryAnchor("Camp", "CampInfo.Id"),
            new("Camp::Foo.CampSettingsInfo", "Camp", "settingsInfoDto", new[] { "CampSettingsInfo.NameLockDate" }, Array.Empty<ConservationAnchorMethod>(), new()),
            ReadIface("Camp"),
        };
        var (now, path) = Setup("Camp", nowAnchors, new(),
            baselineMethods: new() { ("GetNameLockDateAsync", "DateTime") },
            baselinePrimaryPaths: new() { "CampInfo.Id" },
            baselineReadSurface: 6, nowReadSurface: 0, baselineHelpers: new());
        try
        {
            var cmp = SurfaceScoreBaseline.Compare(now, path);
            var v = cmp.ConservationVerdicts.Single(x => x.Section == "Camp");
            Assert.Equal("canonical-consolidation", v.Kind);
            Assert.Contains(v.Methods, m => m.RemovedMethod == "GetNameLockDateAsync" && m.TargetDto == "settingsInfoDto");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Gate_CanonicalConsolidation_DoesNotFlagDtoGrowthAsBagGaming()
    {
        var (now, path) = Setup("Camp",
            nowAnchors: new() { PrimaryAnchor("Camp", "CampInfo.Seasons[].Members[].UserId", "CampInfo.Images[]"), ReadIface("Camp") },
            nowHelpers: new(),
            baselineMethods: new() { ("GetMembersAsync", "List<CampMemberInfo>") },
            baselinePrimaryPaths: new() { "CampInfo.Seasons[].Members[].UserId" },
            baselineReadSurface: 12, nowReadSurface: 6, baselineHelpers: new());
        try
        {
            var cmp = SurfaceScoreBaseline.Compare(now, path);
            Assert.DoesNotContain(cmp.Suspicious, s => s.Kind is "parameter-bag-consolidation" or "godmethod-up-interface-down");
            Assert.Contains(cmp.Suspicious, s => s.Kind == "canonical-consolidation");
        }
        finally { File.Delete(path); }
    }

    // ---------------- helpers ----------------

    private static ConservationAnchor PrimaryAnchor(string section, params string[] paths)
        => new($"{section}::Foo.{section}Info", section, "primaryInfoDto", paths, Array.Empty<ConservationAnchorMethod>(), new());

    private static ConservationAnchor ReadIface(string section, params (string n, string r)[] methods)
        => new($"{section}::Foo.I{section}ServiceRead", section, "readServiceInterface", Array.Empty<string>(),
               methods.Select(m => new ConservationAnchorMethod(m.n, m.r)).ToList(), new());

    private static (ScoreReport now, string baselinePath) Setup(
        string section, List<ConservationAnchor> nowAnchors, List<HelperCandidate> nowHelpers,
        List<(string name, string returns)> baselineMethods, List<string> baselinePrimaryPaths,
        int baselineReadSurface, int nowReadSurface, List<string> baselineHelpers)
    {
        var now = new ScoreReport();
        now.ConservationAnchors.AddRange(nowAnchors);
        now.HelperCandidates.AddRange(nowHelpers);
        var g = new GroupScore { Name = section };
        g.ByRule["readServiceInterfaceMethod"] = nowReadSurface;
        now.Groups[section] = g;

        var baseline = new
        {
            total = baselineReadSurface,
            surfaceTotal = baselineReadSurface,
            internalComplexityTotal = 0,
            byRule = new Dictionary<string, int>(),
            groups = new[]
            {
                new
                {
                    name = section, total = baselineReadSurface, surfaceTotal = baselineReadSurface,
                    internalComplexityTotal = 0,
                    byRule = new Dictionary<string, int> { ["readServiceInterfaceMethod"] = baselineReadSurface }
                }
            },
            conservationAnchors = new object[]
            {
                new { key = $"{section}::Foo.{section}Info", section, role = "primaryInfoDto", paths = baselinePrimaryPaths, methods = Array.Empty<object>() },
                new { key = $"{section}::Foo.I{section}ServiceRead", section, role = "readServiceInterface", paths = Array.Empty<string>(),
                      methods = baselineMethods.Select(m => new { name = m.name, returns = m.returns }).ToArray() },
            },
            helperCandidates = baselineHelpers.Select(h => new { display = h, methods = Array.Empty<string>() }).ToArray()
        };
        var path = Path.GetTempFileName();
        File.WriteAllText(path, JsonSerializer.Serialize(baseline));
        return (now, path);
    }
}
