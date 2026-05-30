using Microsoft.CodeAnalysis;

namespace Reforge.Tests;

/// <summary>
/// Plan B - section-architecture scored rules (crossSectionWriteSurface, missing*,
/// readSurfaceProjectionMethod) + conservationAnchors, exercised end-to-end through
/// SurfaceScoreEngine against the sample solution with an explicit Camp-section config.
/// </summary>
[Collection("SampleSolution")]
public class SectionArchitectureTests
{
    private readonly SampleSolutionFixture _fixture;
    public SectionArchitectureTests(SampleSolutionFixture fixture) => _fixture = fixture;

    private string Dir => LocationHelper.GetSolutionDirectory(_fixture.Solution);

    /// <summary>A config that maps the Camp fixtures into a repo-backed "Camp" section.</summary>
    private static SurfaceScoreConfig CampConfig() => WithDefaults(new SurfaceScoreConfig
    {
        Sections =
        {
            ["Camp"] = new SectionRule
            {
                RepositoryInterfaces = { "ICampRepository" },
                ServiceInterfaces = { "ICampSectionService" },
                ReadServiceInterfaces = { "ICampServiceRead" }
                // primaryInfoDto / settingsInfoDto left to convention -> CampInfo / CampSettingsInfo
            }
        }
    });

    /// <summary>
    /// Merges default classifications + weights into a hand-built section config (as
    /// <see cref="SurfaceScoreConfig.LoadOrDefault"/> does in production) and finalizes the
    /// effective-section list. Section rules need the default weights to score.
    /// </summary>
    private static SurfaceScoreConfig WithDefaults(SurfaceScoreConfig cfg)
    {
        var defaults = SurfaceScoreConfig.Default();
        foreach (var (k, v) in defaults.Classifications) cfg.Classifications.TryAdd(k, v);
        foreach (var (k, v) in defaults.Weights) cfg.Weights.TryAdd(k, v);
        cfg.BuildEffectiveSections();
        return cfg;
    }

    private async Task<ScoreReport> Score(SurfaceScoreConfig cfg)
    {
        var engine = new SurfaceScoreEngine(cfg, Dir);
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
        var report = await Score(CampConfig());

        Assert.True(report.ByRule.TryGetValue("readSurfaceProjectionMethod", out var pts) && pts > 0);
        var camp = report.Groups["Camp"];
        // two charged methods (predicate + projection) x weight 4 = 8
        Assert.Equal(8, camp.ByRule["readSurfaceProjectionMethod"]);
        // surcharge is on the surface axis, not internal complexity
        Assert.False(SurfaceScoreRuleGroups.IsInternalComplexity("readSurfaceProjectionMethod"));
    }

    [Fact]
    public async Task ReadSurfaceProjectionMethod_ExemptsEscapeHatch()
    {
        var cfg = WithDefaults(new SurfaceScoreConfig
        {
            Sections =
            {
                ["Camp"] = new SectionRule
                {
                    RepositoryInterfaces = { "ICampRepository" },
                    ServiceInterfaces = { "ICampSectionService" },
                    ReadServiceInterfaces = { "ICampServiceRead" },
                    EscapeHatchReadMethods =
                    {
                        new EscapeHatchReadMethod { Method = "ICampServiceRead.IsUserCampLeadAsync", Reason = "legacy", Since = "2026-02" }
                    }
                }
            }
        });
        var report = await Score(cfg);

        // only the projection method remains charged -> 4
        Assert.Equal(4, report.Groups["Camp"].ByRule["readSurfaceProjectionMethod"]);
    }

    // ---------------- Task 4: missing* rules (repo-backed gated) ----------------

    [Fact]
    public async Task MissingSurfaceRules_FireOnlyForRepoBackedSections()
    {
        var cfg = WithDefaults(new SurfaceScoreConfig
        {
            Sections =
            {
                ["Lodge"] = new SectionRule { RepositoryInterfaces = { "ILodgeRepository" }, ServiceInterfaces = { "ILodgeService" } },
                ["Dorm"] = new SectionRule { RepositoryInterfaces = { "IDormRepository" }, ReadServiceInterfaces = { "IDormServiceRead" } },
                ["Tent"] = new SectionRule { RepositoryInterfaces = { "ITentRepository" }, ServiceInterfaces = { "ITentService" }, ReadServiceInterfaces = { "ITentServiceRead" } },
                ["Booking"] = new SectionRule { Symbols = { "*Orchestrator" } }
            }
        });
        var report = await Score(cfg);

        Assert.True(report.Groups["Lodge"].ByRule.ContainsKey("missingReadSurface"));
        Assert.True(report.Groups["Dorm"].ByRule.ContainsKey("missingWriteSurface"));
        Assert.True(report.Groups["Tent"].ByRule.ContainsKey("missingPrimaryInfoDto"));

        // Orchestrator-only: none of the missing* rules
        var booking = report.Groups.TryGetValue("Booking", out var b) ? b : null;
        if (booking is not null)
        {
            Assert.False(booking.ByRule.ContainsKey("missingReadSurface"));
            Assert.False(booking.ByRule.ContainsKey("missingWriteSurface"));
            Assert.False(booking.ByRule.ContainsKey("missingPrimaryInfoDto"));
        }

        // Lodge has LodgeInfo + write but no read; must NOT be charged missingPrimaryInfoDto or missingWriteSurface
        Assert.False(report.Groups["Lodge"].ByRule.ContainsKey("missingPrimaryInfoDto"));
        Assert.False(report.Groups["Lodge"].ByRule.ContainsKey("missingWriteSurface"));
    }

    // ---------------- Task 5: crossSectionWriteSurface + unverified advisory + escape analysis ----------------

    private static SurfaceScoreConfig CampReportingConfig(params GrandfatheredDependency[] grandfathered)
    {
        var reporting = new SectionRule { Symbols = { "*ReportBuilder", "*Delegator" } };
        foreach (var g in grandfathered) reporting.GrandfatheredDependencies.Add(g);
        return WithDefaults(new SurfaceScoreConfig
        {
            Sections =
            {
                ["Camp"] = new SectionRule
                {
                    RepositoryInterfaces = { "ICampRepository" },
                    ServiceInterfaces = { "ICampSectionService" },
                    ReadServiceInterfaces = { "ICampServiceRead" }
                },
                ["Reporting"] = reporting
            }
        });
    }

    [Fact]
    public async Task CrossSectionWriteSurface_FiresOnCrossSectionReadOnlyConsumer_SuppressesGeneric()
    {
        var report = await Score(CampReportingConfig());

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
        var report = await Score(CampReportingConfig());

        // CampDelegator passes the dep onward -> NOT a confident crossSectionWriteSurface; an advisory diagnostic instead.
        var reporting = report.Groups["Reporting"];
        Assert.DoesNotContain(reporting.Entries, e => e.Rule == "crossSectionWriteSurface" && e.Symbol == "CampDelegator");
        Assert.Contains(report.Diagnostics, d => d.Code == "crossSectionWriteSurfaceUnverified" && d.Message.Contains("CampDelegator"));
    }

    [Fact]
    public async Task CrossSectionWriteSurface_GrandfatheredDependency_IsSuppressed()
    {
        var report = await Score(CampReportingConfig(
            new GrandfatheredDependency { Dependency = "CampReportBuilder->ICampSectionService", Reason = "legacy", Since = "2026-03", Owner = "camps" }));

        Assert.DoesNotContain(report.Groups["Reporting"].Entries, e => e.Rule == "crossSectionWriteSurface" && e.Symbol == "CampReportBuilder");
    }

    // ---------------- Task 6: conservationAnchors emission ----------------

    [Fact]
    public async Task ConservationAnchors_EmittedFqKeyedWithRecursivePaths()
    {
        var report = await Score(CampConfig());

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
        var report = await Score(CampConfig());

        Assert.NotEmpty(report.ConservationAnchors);
        Assert.Contains(report.ConservationAnchors, a => a.Role == "readServiceInterface");
        Assert.Contains(report.ConservationAnchors, a => a.Role == "primaryInfoDto");
    }
}
