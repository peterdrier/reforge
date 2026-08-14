using System.Text.Json;

namespace Reforge.Tests;

[Collection("SampleSolution")]
public class SurfaceScoreTests
{
    private readonly SampleSolutionFixture _fixture;

    public SurfaceScoreTests(SampleSolutionFixture fixture)
    {
        _fixture = fixture;
    }

    // ---------------- Config loading ----------------

    [Fact]
    public void LoadOrDefault_NoFile_ReturnsDefaults()
    {
        var dir = Path.Combine(Path.GetTempPath(), "reforge-surface-score-test-no-file");
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        Directory.CreateDirectory(dir);

        var cfg = SurfaceScoreConfig.LoadOrDefault(null, dir, out var loadedFrom);

        Assert.Null(loadedFrom);
        Assert.Empty(cfg.Sections);            // sections are assemblies; config carries policy only
        Assert.NotEmpty(cfg.Classifications);  // defaults present
        Assert.NotEmpty(cfg.Weights);          // defaults present
    }

    [Fact]
    public void LoadOrDefault_ParsesSectionPolicy()
    {
        var dir = Path.Combine(Path.GetTempPath(), "reforge-surface-score-test-sections");
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        Directory.CreateDirectory(dir);
        var configPath = Path.Combine(dir, "reforge.surface-score.json");
        File.WriteAllText(configPath, """
            {
              "sections": {
                "Users": {
                  "primaryInfoDto": "UserInfo"
                },
                "Orders": {
                  "requiresReadSurface": false
                }
              }
            }
            """);

        var cfg = SurfaceScoreConfig.LoadOrDefault(configPath, dir, out var loadedFrom);

        Assert.Equal(configPath, loadedFrom);
        Assert.Equal("UserInfo", cfg.Policy("Users").PrimaryInfoDto);
        Assert.False(cfg.Policy("Orders").RequiresReadSurface);
        // A section with no policy block still resolves — to the shared empty policy.
        Assert.Same(SectionRule.None, cfg.Policy("Nope"));
    }

    [Fact]
    public void LoadOrDefault_PolicyKeys_MatchSectionNamesCaseInsensitively()
    {
        // System.Text.Json assigns new dictionaries through the setters, dropping the
        // OrdinalIgnoreCase comparers from the field initializers. Section names are now derived
        // from assembly names, so a hand-written "camp" key has to reach the derived "Camp".
        var dir = Path.Combine(Path.GetTempPath(), "reforge-surface-score-test-case");
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        Directory.CreateDirectory(dir);
        var configPath = Path.Combine(dir, "reforge.surface-score.json");
        File.WriteAllText(configPath, """
            {
              "sections": { "camp": { "primaryInfoDto": "CampInfo" } },
              "weights": { "CROSSSECTIONFULLSERVICE": 7 },
              "classifications": { "ENTITY": { "namePatterns": ["Zzz$"] } }
            }
            """);

        var cfg = SurfaceScoreConfig.LoadOrDefault(configPath, dir, out _);

        Assert.Equal("CampInfo", cfg.Policy("Camp").PrimaryInfoDto);
        Assert.Equal(7, cfg.Weight("crossSectionFullService"));
        // The case-variant override must REPLACE the default, not sit beside it as a second entry.
        Assert.Single(cfg.Classifications, c => string.Equals(c.Key, "entity", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void LoadOrDefault_RetiredMatcherKeys_AreIgnoredNotFatal()
    {
        // v0.22 and earlier described section membership with paths/namespaces/symbols and a
        // legacy `groups` array. Those keys are gone; a stale config must still load (the section
        // simply groups by assembly now) rather than throwing on the way in.
        var dir = Path.Combine(Path.GetTempPath(), "reforge-surface-score-test-legacy");
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        Directory.CreateDirectory(dir);
        var configPath = Path.Combine(dir, "reforge.surface-score.json");
        File.WriteAllText(configPath, """
            {
              "groups": [ { "name": "Legacy", "match": { "paths": ["**/Legacy/**"] } } ],
              "resources": { "dbSets": { "ownerByName": { "Users": "Legacy" } } },
              "sections": {
                "Users": {
                  "paths": ["**/SampleSolution.Services/User*.cs"],
                  "symbols": ["IUser*"],
                  "repositoryInterfaces": ["IUserRepository"],
                  "primaryInfoDto": "UserInfo"
                }
              }
            }
            """);

        var cfg = SurfaceScoreConfig.LoadOrDefault(configPath, dir, out _);

        Assert.Equal("UserInfo", cfg.Policy("Users").PrimaryInfoDto);  // policy survives
        Assert.Single(cfg.Sections);
    }

    // ---------------- Engine behavior ----------------

    [Fact]
    public async Task Grouping_IsByAssembly_WithoutAnyConfig()
    {
        var report = await ScoreDefaultAsync();

        // One section per non-test assembly, ".Contracts" folded into its parent.
        Assert.Equal(
            new[] { "Camp", "Core", "Dorm", "Lodge", "Reporting", "Services", "Tent", "Web" },
            report.ConfiguredSections.ToArray());
        Assert.All(report.Groups.Keys, k => Assert.Contains(k, report.ConfiguredSections));
    }

    [Fact]
    public async Task ContractsAssembly_ScoresIntoItsParentSection()
    {
        var report = await ScoreDefaultAsync();

        // ICampServiceRead lives in SampleSolution.Camp.Contracts; its methods must be charged
        // to Camp, and no "Camp.Contracts" group may exist.
        Assert.DoesNotContain(report.Groups.Keys, k => k.Contains("Contracts", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(report.Groups["Camp"].Entries,
            e => e.Rule == "readServiceInterfaceMethod" && e.Symbol == "GetByIdAsync");
    }

    [Fact]
    public async Task CrossSectionRepository_FiresAcrossAnAssemblyBoundary()
    {
        // UserService (SampleSolution.Services) injects IUserRepository (SampleSolution.Core).
        var report = await ScoreDefaultAsync();

        var crossRepo = report.Groups["Services"].Entries.Where(e => e.Rule == "crossSectionRepository").ToList();
        Assert.NotEmpty(crossRepo);
        Assert.Contains(crossRepo, e => e.Detail is not null && e.Detail.Contains("IUserRepository", StringComparison.Ordinal));
    }

    // ---------------- Diagnostic ----------------

    [Fact]
    public async Task MissingGroup_ProducesDiagnostic_WhenNothingMatches()
    {
        var report = await ScoreDefaultAsync();

        // Simulate the command-layer diagnostic — the engine itself doesn't add it, the command
        // wrapper does. Replicate the check here so the assertion lives near the rule.
        const string requested = "TotallyMadeUpSection";
        Assert.False(report.Groups.ContainsKey(requested));
        Assert.DoesNotContain(requested, report.ConfiguredSections);
        // The contract: when both are false, the command emits a "group-not-found" diagnostic
        // listing report.ConfiguredSections (the solution's assemblies).
    }

    // ---------------- Stale config section policy ----------------

    [Fact]
    public async Task ScoreAsync_StaleSectionPolicy_IsInertAndReported()
    {
        // A policy block keyed to an assembly that no longer exists must not reach into scoring,
        // and must be named rather than quietly dropped.
        var cfg = SurfaceScoreConfig.Default();
        cfg.Sections["GhostSectionThatNoAssemblyProduces"] = new SectionRule
        {
            PrimaryInfoDto = "UserDto"
        };
        var engine = new SurfaceScoreEngine(cfg, LocationHelper.GetSolutionDirectory(_fixture.Solution));
        var report = await engine.ScoreAsync(_fixture.Solution, CancellationToken.None);

        var diagnostic = Assert.Single(report.Diagnostics, d => d.Code == "unknown-config-section");
        Assert.Contains("GhostSectionThatNoAssemblyProduces", diagnostic.Message);
        Assert.DoesNotContain("GhostSectionThatNoAssemblyProduces", report.ConfiguredSections);
    }

    [Fact]
    public async Task ScoreAsync_LiveSectionPolicy_IsNotReportedAsUnknown()
    {
        var cfg = SurfaceScoreConfig.Default();
        cfg.Sections["Camp"] = new SectionRule { PrimaryInfoDto = "CampInfo" };
        var engine = new SurfaceScoreEngine(cfg, LocationHelper.GetSolutionDirectory(_fixture.Solution));
        var report = await engine.ScoreAsync(_fixture.Solution, CancellationToken.None);

        Assert.DoesNotContain(report.Diagnostics, d => d.Code == "unknown-config-section");
    }

    // ---------------- Rule glossary ----------------

    [Fact]
    public async Task RuleGlossary_HasEntryForEveryFiredRule()
    {
        var cfg = SurfaceScoreConfig.Default();
        var engine = new SurfaceScoreEngine(cfg, LocationHelper.GetSolutionDirectory(_fixture.Solution));
        var report = await engine.ScoreAsync(_fixture.Solution, CancellationToken.None);

        Assert.NotEmpty(report.ByRule);
        // Every rule that contributed to the score must have a factual description so an agent
        // reading the JSON output knows what each key means without external lookup.
        foreach (var rule in report.ByRule.Keys)
        {
            Assert.True(SurfaceScoreRuleGlossary.Descriptions.ContainsKey(rule),
                $"Rule '{rule}' fired but has no glossary entry. Add one to SurfaceScoreRuleGlossary.");
        }
    }

    [Fact]
    public void RuleGlossary_DescriptionsAreFactualNotAdvisory()
    {
        // Descriptions state WHAT triggers the rule (factual), never WHAT TO DO. Catch
        // accidental advice in glossary contributions by scanning for imperative-mood
        // prefixes that would indicate a recommendation slipped in.
        string[] adviceMarkers =
        {
            "consider ", "you should", "should be ", "instead of",
            "switch to ", "use ", "prefer ", "refactor", "extract ", "split ",
            "rename ", "move ", "replace "
        };

        foreach (var (rule, desc) in SurfaceScoreRuleGlossary.Descriptions)
        {
            var lower = desc.ToLowerInvariant();
            foreach (var marker in adviceMarkers)
            {
                Assert.False(lower.Contains(marker, StringComparison.Ordinal),
                    $"Glossary entry for '{rule}' looks advisory ('{marker}'): {desc}");
            }
        }
    }

    // ---------------- Symbol aggregation ----------------

    [Fact]
    public async Task SymbolAggregation_CollapsesEntriesAcrossRulesPerSymbol()
    {
        // Verify the engine actually emits multiple rule entries for some single symbols
        // (the precondition for cross-rule aggregation to be useful), and that those entries
        // are addressable per symbol+file.
        var cfg = SurfaceScoreConfig.Default();
        var engine = new SurfaceScoreEngine(cfg, LocationHelper.GetSolutionDirectory(_fixture.Solution));
        var report = await engine.ScoreAsync(_fixture.Solution, CancellationToken.None);

        var allEntries = report.Groups.Values.SelectMany(g => g.Entries).ToList();
        var bySymbol = allEntries
            .GroupBy(e => $"{e.Symbol}|{e.File}", StringComparer.Ordinal)
            .Select(g => new
            {
                Key = g.Key,
                RuleCount = g.Select(e => e.Rule).Distinct().Count(),
                Total = g.Sum(e => e.Points)
            })
            .OrderByDescending(x => x.RuleCount)
            .ToList();

        // At least one symbol should fire across multiple rules — that's the combinatorial
        // refactoring opportunity the aggregation surfaces.
        Assert.NotEmpty(bySymbol);
        Assert.Contains(bySymbol, x => x.RuleCount >= 2);
    }

    // ---------------- Canonical DTOs ----------------

    [Fact]
    public async Task CanonicalReadDto_ReturnTypeCredits()
    {
        // No config: the credit applies solution-wide off the DERIVED set. CampFeedReader lives in
        // Reporting and returns CampInfo, which Camp exports from its .Contracts assembly.
        var report = await ScoreDefaultAsync();

        var canonical = report.Groups["Reporting"].Entries.Where(e => e.Rule == "canonicalReadDtoReturn").ToList();
        Assert.NotEmpty(canonical);
        Assert.Contains(canonical, e => e.Detail?.Contains("-> CampInfo", StringComparison.Ordinal) == true);
        Assert.All(canonical, e => Assert.True(e.Points < 0, "canonicalReadDtoReturn must contribute a credit (negative points)"));
    }

    [Fact]
    public async Task CanonicalReadDto_NoCreditForDtosOffTheContractsSurface()
    {
        // CampLegacyEntity is public and exported, but declared in SampleSolution.Camp with no
        // Contracts/ folder above it. Camp never published it, so returning it earns nothing.
        var report = await ScoreDefaultAsync();

        Assert.DoesNotContain(report.Groups["Reporting"].Entries, e => e.Rule == "canonicalReadDtoReturn"
            && e.Detail?.Contains("CampLegacyEntity", StringComparison.Ordinal) == true);
    }

    // ---------------- methodReturnsEntityAcrossSection ----------------

    [Fact]
    public async Task MethodReturnsEntityAcrossSection_FiresWhenReturnTypeLivesInDifferentSection()
    {
        // User lives in SampleSolution.Core/Models (matched by the default `entity`
        // classification's "**/Models/**" path) — i.e. section Core. UserService lives in
        // SampleSolution.Services, so GetUserAsync returning User leaks an entity across the
        // assembly boundary. No config needed to see it.
        var report = await ScoreDefaultAsync();

        var leaks = report.Groups["Services"].Entries
            .Where(e => e.Rule == "methodReturnsEntityAcrossSection")
            .ToList();
        Assert.NotEmpty(leaks);
        // At least one should reference returning User (the canonical entity in the sample).
        Assert.Contains(leaks, e => e.Detail?.Contains("User", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task MethodReturnsEntityAcrossSection_ExemptsCanonicalDtos()
    {
        // CampStayEntity and CampLegacyEntity are the same shape and both classified `entity` by
        // name; only CampStayEntity is exported from Camp's contracts assembly. CampFeedReader
        // (Reporting) returns both across the boundary — the published one is credited, the other
        // is charged. No config decides this.
        var report = await ScoreDefaultAsync();
        var reporting = report.Groups["Reporting"];

        Assert.DoesNotContain(reporting.Entries, e => e.Rule == "methodReturnsEntityAcrossSection"
            && e.Detail?.Contains("-> CampStayEntity", StringComparison.Ordinal) == true);
        Assert.Contains(reporting.Entries, e => e.Rule == "canonicalReadDtoReturn"
            && e.Detail?.Contains("-> CampStayEntity", StringComparison.Ordinal) == true);

        Assert.Contains(reporting.Entries, e => e.Rule == "methodReturnsEntityAcrossSection"
            && e.Detail?.Contains("-> CampLegacyEntity", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task RemovedCanonicalReadDtosField_IsReportedNotSilentlyIgnored()
    {
        // The field is gone, and System.Text.Json would drop it without a word — but a config that
        // still carries it used to grant credit and suppress the entity penalty solution-wide.
        var dir = Path.Combine(Path.GetTempPath(), "reforge-surface-score-test-removed-field");
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        Directory.CreateDirectory(dir);
        var configPath = Path.Combine(dir, "reforge.surface-score.json");
        File.WriteAllText(configPath, """
            { "sections": { "Camp": { "canonicalReadDtos": ["CampInfo"] } } }
            """);

        var cfg = SurfaceScoreConfig.LoadOrDefault(configPath, dir, out _);
        Assert.True(cfg.Policy("Camp").DeclaresRemovedCanonicalReadDtos);

        var engine = new SurfaceScoreEngine(cfg, LocationHelper.GetSolutionDirectory(_fixture.Solution));
        var report = await engine.ScoreAsync(_fixture.Solution, CancellationToken.None);

        var diagnostic = Assert.Single(report.Diagnostics, d => d.Code == "removed-config-field");
        Assert.Contains("canonicalReadDtos", diagnostic.Message);
        Assert.Contains("Camp", diagnostic.Message);
    }

    // ---------------- duplicateDbSetOwner ----------------

    [Fact]
    public async Task DuplicateDbSetOwner_DerivesOwnershipFromTheDeclaringContextsAssembly()
    {
        // AppDbContext (SampleSolution.Services) declares the Users DbSet, so Users belongs to
        // Services. BadController (SampleSolution.Web) touches it directly -> second owner.
        // No ownership map in config: the owner is read off the model.
        var report = await ScoreDefaultAsync();

        Assert.Contains(report.Groups["Web"].Entries,
            e => e.Rule == "duplicateDbSetOwner" && e.Symbol == "BadController");
        Assert.Contains(report.DuplicateOwners, d => d.Contains("Users", StringComparison.Ordinal)
                                                 && d.Contains("owner: Services", StringComparison.Ordinal));

        // The declaring section itself is never its own duplicate.
        Assert.DoesNotContain(report.Groups["Services"].Entries, e => e.Rule == "duplicateDbSetOwner");
    }

    // ---------------- helperCandidates (conservation gate) ----------------

    [Fact]
    public async Task HelperCandidates_IncludeStatelessSinks()
    {
        var report = await ScoreDefaultAsync();
        // A static helper class with public methods is a helper candidate; a service with
        // instance fields (UserService) is not.
        Assert.Contains(report.HelperCandidates, h => h.Display.EndsWith("CampReadModelProjection"));
        Assert.DoesNotContain(report.HelperCandidates, h => h.Display.EndsWith("UserService"));
    }

    // ---------------- writeCapableInterfaceUsedReadOnly ----------------

    [Fact]
    public async Task WriteCapableUsedReadOnly_FiresOnReadOnlyConsumerOfFullInterface()
    {
        // IGreetingService inherits IGreetingServiceRead and adds RecordGreetingAsync (write).
        // SameSectionGreetingConsumer (same assembly as the interface) holds the full interface
        // but only calls Get methods that also exist on the read interface — the generic rule
        // fires. FullGreetingConsumer calls RecordGreetingAsync (write) — it must NOT fire.
        // ReadOnlyGreetingConsumer sits in another assembly, so the cross-section specialization
        // claims it instead (asserted below).
        var report = await ScoreDefaultAsync();

        var readOnly = AllEntries(report).Where(e => e.Rule == "writeCapableInterfaceUsedReadOnly").ToList();
        Assert.Contains(readOnly, e => e.Symbol == "SameSectionGreetingConsumer");
        Assert.DoesNotContain(readOnly, e => e.Symbol == "FullGreetingConsumer");

        Assert.DoesNotContain(readOnly, e => e.Symbol == "ReadOnlyGreetingConsumer");
        Assert.Contains(AllEntries(report),
            e => e.Rule == "crossSectionWriteSurface" && e.Symbol == "ReadOnlyGreetingConsumer");
    }

    // ---------------- Internal complexity: dispatcher / read-shape ----------------

    [Fact]
    public async Task GenericActionDispatcher_FiresOnImplAndInterface()
    {
        // SignupWorkflowService.ApplyAsync: generic verb + SignupAction enum + switch that
        // delegates arms to distinct members. Must fire genericActionDispatcher AND be
        // attributed to the interface method it implements.
        var report = await ScoreDefaultAsync();
        var gad = AllEntries(report).Where(e => e.Rule == "genericActionDispatcher" && e.Symbol == "ApplyAsync").ToList();
        Assert.True(gad.Count >= 2, $"expected genericActionDispatcher on both impl and interface ApplyAsync; got {gad.Count}");
        // It must NOT also be double-counted as the plain actionDispatcher.
        Assert.DoesNotContain(AllEntries(report), e => e.Rule == "actionDispatcher" && e.Symbol == "ApplyAsync");
    }

    [Fact]
    public async Task ActionDispatcher_FiresOnNonGenericStructuralDispatch()
    {
        // RouteService.RouteAsync dispatches structurally but its name isn't a generic verb.
        var report = await ScoreDefaultAsync();
        var entries = AllEntries(report).ToList();
        Assert.Contains(entries, e => e.Rule == "actionDispatcher" && e.Symbol == "RouteAsync");
        Assert.DoesNotContain(entries, e => e.Rule == "genericActionDispatcher" && e.Symbol == "RouteAsync");
    }

    [Fact]
    public async Task MutationModeParameter_FiresOnInlineModeMutation()
    {
        // ThingService.CreateThingAsync: generic verb + mode enum but inline (no delegation).
        var report = await ScoreDefaultAsync();
        var entries = AllEntries(report).ToList();
        Assert.Contains(entries, e => e.Rule == "mutationModeParameter" && e.Symbol == "CreateThingAsync");
        Assert.DoesNotContain(entries, e => e.Rule == "genericActionDispatcher" && e.Symbol == "CreateThingAsync");
        Assert.DoesNotContain(entries, e => e.Rule == "actionDispatcher" && e.Symbol == "CreateThingAsync");
    }

    [Fact]
    public async Task StateEngine_IsExemptFromAllDispatcherRules()
    {
        // WorkflowService.ApplyWorkflowTransitionAsync switches on an action enum but validates
        // current state and uses transition vocabulary — a real state machine, exempt.
        var report = await ScoreDefaultAsync();
        var entries = AllEntries(report).Where(e => e.Symbol == "ApplyWorkflowTransitionAsync").ToList();
        Assert.DoesNotContain(entries, e => e.Rule == "genericActionDispatcher");
        Assert.DoesNotContain(entries, e => e.Rule == "actionDispatcher");
        Assert.DoesNotContain(entries, e => e.Rule == "mutationModeParameter");
    }

    [Fact]
    public async Task ActionDispatcher_DoesNotFireOnReadShapeConsolidation()
    {
        // GreetingQueryService.GetGreetingsAsync switches on a read-shape enum but returns
        // data (a read) and its arms share a base query. The behavioral gate must exempt it.
        var report = await ScoreDefaultAsync();
        var entries = AllEntries(report).Where(e => e.Rule == "actionDispatcher").ToList();
        Assert.DoesNotContain(entries, e => e.Symbol == "GetGreetingsAsync");
    }

    [Fact]
    public async Task GodMethod_FiresLongMethodAndCognitiveComplexity()
    {
        var report = await ScoreDefaultAsync();
        var entries = AllEntries(report).ToList();
        Assert.Contains(entries, e => e.Rule == "longMethod" && e.Symbol == "BuildEverything");
        Assert.Contains(entries, e => e.Rule == "cognitiveComplexity" && e.Symbol == "BuildEverything");
    }

    [Fact]
    public async Task FlagsControlFlow_FiresOnFlagsDrivenMutation_ButNotAsDispatcher()
    {
        var report = await ScoreDefaultAsync();
        var entries = AllEntries(report).ToList();
        Assert.Contains(entries, e => e.Rule == "flagsControlFlow" && e.Symbol == "UpdateAsync");
        Assert.DoesNotContain(entries, e => e.Rule == "actionDispatcher" && e.Symbol == "UpdateAsync");
        // [Flags] is owned by flagsControlFlow — must not also fire mutationModeParameter.
        Assert.DoesNotContain(entries, e => e.Rule == "mutationModeParameter" && e.Symbol == "UpdateAsync");
    }

    [Fact]
    public async Task ScoreAxes_AreTrackedSeparately_AndCombinedIsTheirSum()
    {
        var report = await ScoreDefaultAsync();
        Assert.True(report.SurfaceTotal > 0, "expected surface points");
        Assert.True(report.InternalComplexityTotal > 0, "expected internal-complexity points from fixtures");
        Assert.Equal(report.SurfaceTotal + report.InternalComplexityTotal, report.Total);
    }

    // ---------------- Boundary-input surface ----------------

    [Fact]
    public async Task PublicInputWithHiddenState_FiresOnInternalGetterInput()
    {
        // CampRegistrationInput: public, used as a public-method param, all 6 members internal.
        var report = await ScoreDefaultAsync();
        var entries = AllEntries(report).ToList();
        Assert.Contains(entries, e => e.Rule == "publicInputWithHiddenState" && e.Symbol == "CampRegistrationInput");
        Assert.Contains(entries, e => e.Rule == "parameterBagInput" && e.Symbol == "CampRegistrationInput");
    }

    [Fact]
    public async Task InlineParameterObjectConstruction_FiresAtCallSite()
    {
        var report = await ScoreDefaultAsync();
        Assert.Contains(AllEntries(report),
            e => e.Rule == "inlineParameterObjectConstruction" && e.Symbol == "CampRegistrationInput");
    }

    [Fact]
    public async Task GoodRequestRecord_WithPublicStateAndValidation_IsNotPenalized()
    {
        // CampRegistrationRequest: public readable record + Validate() behavior. None of the
        // boundary-input rules should fire on it.
        var report = await ScoreDefaultAsync();
        var entries = AllEntries(report).Where(e => e.Symbol == "CampRegistrationRequest").ToList();
        Assert.DoesNotContain(entries, e => e.Rule == "publicInputWithHiddenState");
        Assert.DoesNotContain(entries, e => e.Rule == "parameterBagInput");
    }

    [Fact]
    public async Task BoundaryInputRules_AreOnSurfaceAxis_NotInternalComplexity()
    {
        // The whole point: these counter a surface reduction, so they must land on surface.
        var report = await ScoreDefaultAsync();
        foreach (var rule in new[] { "publicInputWithHiddenState", "parameterBagInput", "inlineParameterObjectConstruction" })
            Assert.False(SurfaceScoreRuleGroups.IsInternalComplexity(rule), $"{rule} must be a surface rule");
    }

    [Fact]
    public void Pareto_ParameterBagConsolidation_IsFlaggedEvenWhenInternalFlat()
    {
        // methodParameterOverflow falls (signature shortened) but equivalent input-bag surface
        // appears — flagged regardless of verdict, even with internal complexity unchanged.
        var basePath = WriteBaselineJson(new
        {
            total = 1000,
            surfaceTotal = 1000,
            internalComplexityTotal = 0,
            byRule = new Dictionary<string, int> { ["methodParameterOverflow"] = 30 }
        });

        var now = new ScoreReport { SurfaceTotal = 1010, InternalComplexityTotal = 0, Total = 1010 };
        now.ByRule["methodParameterOverflow"] = 6;
        now.ByRule["parameterBagInput"] = 24;
        now.ByRule["publicInputWithHiddenState"] = 23;
        var g = new GroupScore { Name = "Camps", SurfaceTotal = 1010, InternalComplexityTotal = 0 };
        g.ByRule["methodParameterOverflow"] = 6;
        g.ByRule["parameterBagInput"] = 24;
        g.Entries.Add(new ScoreEntry("parameterBagInput", 24, "CampRegistrationInput", "Camps", "CampFixtures.cs", 1, "CampRegistrationInput"));
        now.Groups["Camps"] = g;

        var cmp = SurfaceScoreBaseline.Compare(now, basePath);

        Assert.Contains(cmp.Suspicious, s => s.Kind == "parameter-bag-consolidation"
            && s.Message.Contains("CampRegistrationInput", StringComparison.Ordinal));
    }

    // ---------------- Pareto gate (baseline comparison) ----------------

    [Fact]
    public void Pareto_SurfaceDownComplexityUp_IsTradedAndSuspicious()
    {
        // Bad consolidation: surface dropped 48 but complexity rose 63, driven by longMethod
        // (a god-method growth, not a dispatcher) — so the kind is complexity-traded-for-surface.
        var basePath = WriteBaseline(surface: 1000, internalC: 50, byRule: new() { ["applicationServiceMethod"] = 80 });
        var now = MakeReport(surface: 952, internalC: 113, byRule: new() { ["applicationServiceMethod"] = 80, ["longMethod"] = 63 });

        var cmp = SurfaceScoreBaseline.Compare(now, basePath);

        Assert.Equal("traded", cmp.Solution.Verdict);
        Assert.False(cmp.Solution.Improvement);
        Assert.Contains(cmp.Suspicious, s => s.Kind == "complexity-traded-for-surface");
    }

    [Fact]
    public void Pareto_SurfaceDownComplexityFlat_IsImprovementWithNoSuspicion()
    {
        // Good DTO consolidation: surface dropped, complexity unchanged.
        var basePath = WriteBaseline(surface: 1000, internalC: 50, byRule: new() { ["applicationServiceMethod"] = 80 });
        var now = MakeReport(surface: 990, internalC: 50, byRule: new() { ["applicationServiceMethod"] = 70 });

        var cmp = SurfaceScoreBaseline.Compare(now, basePath);

        Assert.Equal("improved", cmp.Solution.Verdict);
        Assert.True(cmp.Solution.Improvement);
        Assert.Empty(cmp.Suspicious);
    }

    [Fact]
    public void Pareto_TradedWithSmallInternalRise_StillEmitsSuspicious_WithSymbolAttribution()
    {
        // Regression for PR #820 a51bfc62b: surfaceDelta -80, internalDelta only +10, verdict
        // traded — but suspiciousImprovements was empty (old threshold gated it out). A traded
        // verdict must ALWAYS be non-empty AND name the dispatcher symbol.
        var basePath = WriteBaselineJson(new
        {
            total = 1050,
            surfaceTotal = 1000,
            internalComplexityTotal = 50,
            byRule = new Dictionary<string, int> { ["fullServiceInterfaceMethod"] = 200 },
            groups = new[]
            {
                new { name = "Shifts", surfaceTotal = 300, internalComplexityTotal = 50, byRule = new Dictionary<string, int> { ["fullServiceInterfaceMethod"] = 120 } }
            }
        });

        var now = new ScoreReport { SurfaceTotal = 920, InternalComplexityTotal = 60, Total = 980 };
        now.ByRule["fullServiceInterfaceMethod"] = 120;
        now.ByRule["genericActionDispatcher"] = 40;
        var g = new GroupScore { Name = "Shifts", SurfaceTotal = 220, InternalComplexityTotal = 60 };
        g.ByRule["genericActionDispatcher"] = 40;
        g.Entries.Add(new ScoreEntry("genericActionDispatcher", 40, "ApplySignupActionAsync", "Shifts", "ShiftSignupService.cs", 42, "ApplySignupActionAsync (3-arm generic dispatch)"));
        now.Groups["Shifts"] = g;

        var cmp = SurfaceScoreBaseline.Compare(now, basePath);

        Assert.NotEmpty(cmp.Suspicious);
        Assert.Contains(cmp.Suspicious, s => s.Message.Contains("ApplySignupActionAsync", StringComparison.Ordinal));
        Assert.Contains(cmp.Suspicious, s => s.Scope == "Shifts" && s.Kind == "generic-dispatcher-consolidation");
    }

    [Fact]
    public void Pareto_MethodSurfaceDownDispatcherUp_FlagsConsolidation()
    {
        // The sneaky non-traded case: complexity net IMPROVES (longMethod fell more than the
        // dispatcher rose), so the verdict isn't "traded" — but a dispatcher appeared while
        // public method surface shrank. The early-warning detector must still flag it.
        var basePath = WriteBaseline(surface: 1000, internalC: 50, byRule: new() { ["applicationServiceMethod"] = 60, ["longMethod"] = 50 });
        var now = MakeReport(surface: 985, internalC: 40, byRule: new() { ["applicationServiceMethod"] = 30, ["actionDispatcher"] = 30, ["longMethod"] = 10 });

        var cmp = SurfaceScoreBaseline.Compare(now, basePath);

        Assert.Equal("improved", cmp.Solution.Verdict); // net complexity fell
        Assert.Contains(cmp.Suspicious, s => s.Kind == "dispatcher-up-methods-down");
    }

    // ---------------- helpers ----------------

    private async Task<ScoreReport> ScoreDefaultAsync()
    {
        var cfg = SurfaceScoreConfig.Default();
        var engine = new SurfaceScoreEngine(cfg, LocationHelper.GetSolutionDirectory(_fixture.Solution));
        return await engine.ScoreAsync(_fixture.Solution, CancellationToken.None);
    }

    private static IEnumerable<ScoreEntry> AllEntries(ScoreReport report)
        => report.Groups.Values.SelectMany(g => g.Entries);

    private static ScoreReport MakeReport(int surface, int internalC, Dictionary<string, int> byRule)
    {
        var r = new ScoreReport { SurfaceTotal = surface, InternalComplexityTotal = internalC, Total = surface + internalC };
        foreach (var (k, v) in byRule) r.ByRule[k] = v;
        return r;
    }

    private static string WriteBaselineJson(object payload)
    {
        var path = Path.Combine(Path.GetTempPath(), $"reforge-baseline-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(payload));
        return path;
    }

    private static string WriteBaseline(int surface, int internalC, Dictionary<string, int> byRule)
    {
        var path = Path.Combine(Path.GetTempPath(), $"reforge-baseline-{Guid.NewGuid():N}.json");
        var payload = new
        {
            total = surface + internalC,
            surfaceTotal = surface,
            internalComplexityTotal = internalC,
            combinedTotal = surface + internalC,
            byRule,
            groups = Array.Empty<object>()
        };
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(payload));
        return path;
    }

    [Fact]
    public void LoadOrDefault_ParsesSectionMetadata()
    {
        var dir = Path.Combine(Path.GetTempPath(), "reforge-surface-score-test-metadata");
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        Directory.CreateDirectory(dir);
        var configPath = Path.Combine(dir, "reforge.surface-score.json");
        File.WriteAllText(configPath, """
            {
              "sections": {
                "Camp": {
                  "primaryInfoDto": "CampInfo",
                  "settingsInfoDto": "CampSettingsInfo",
                  "cacheDto": "CampInfo",
                  "readShards": [ { "name": "ShiftsByRota", "purpose": "rota-scoped" } ],
                  "requiresReadSurface": true,
                  "grandfatheredDependencies": [
                    { "dependency": "PlacementService->ICampService", "reason": "legacy", "since": "2026-03", "owner": "camps" }
                  ],
                  "escapeHatchReadMethods": [
                    { "method": "ICampServiceRead.MigrateLegacy*", "reason": "one-shot", "since": "2026-02" }
                  ]
                }
              }
            }
            """);

        var cfg = SurfaceScoreConfig.LoadOrDefault(configPath, dir, out _);
        var camp = cfg.Policy("Camp");

        Assert.Equal("CampInfo", camp.PrimaryInfoDto);
        Assert.Equal("CampSettingsInfo", camp.SettingsInfoDto);
        Assert.Equal("CampInfo", camp.CacheDto);
        Assert.Equal("ShiftsByRota", camp.ReadShards.Single().Name);
        Assert.Equal("rota-scoped", camp.ReadShards.Single().Purpose);
        Assert.True(camp.RequiresReadSurface);
        Assert.Equal("PlacementService->ICampService", camp.GrandfatheredDependencies.Single().Dependency);
        Assert.Equal("legacy", camp.GrandfatheredDependencies.Single().Reason);
        Assert.Equal("ICampServiceRead.MigrateLegacy*", camp.EscapeHatchReadMethods.Single().Method);
    }

    [Fact]
    public async Task PolicyForAnAssemblyThatDoesNotExist_CreatesNoSection()
    {
        // Policy can't conjure a section any more — only an assembly can. A stale policy block
        // (section renamed or deleted) is inert, not a phantom group.
        var cfg = SurfaceScoreConfig.Default();
        cfg.Sections["DefinitelyEmpty"] = new SectionRule { PrimaryInfoDto = "NothingInfo" };

        var engine = new SurfaceScoreEngine(cfg, LocationHelper.GetSolutionDirectory(_fixture.Solution));
        var report = await engine.ScoreAsync(_fixture.Solution, CancellationToken.None);

        Assert.False(report.Groups.ContainsKey("DefinitelyEmpty"));
        Assert.DoesNotContain("DefinitelyEmpty", report.ConfiguredSections);
    }

    // ---------------- Build health ----------------

    [Fact]
    public async Task ScoreAsync_BuiltSampleSolution_IsNotDegraded()
    {
        var dir = LocationHelper.GetSolutionDirectory(_fixture.Solution);
        var config = SurfaceScoreConfig.LoadOrDefault(null, dir, out _);
        var engine = new SurfaceScoreEngine(config, dir);

        var report = await engine.ScoreAsync(_fixture.Solution, CancellationToken.None);

        Assert.NotNull(report.BuildHealth);
        Assert.False(report.BuildHealth.Degraded);
        Assert.Equal(0, report.BuildHealth.CompilationErrorCount);
        // Clean build: no captured per-error detail, nothing truncated.
        Assert.Empty(report.BuildHealth.Diagnostics);
        Assert.Equal(0, report.BuildHealth.DiagnosticsTruncated);
    }
}
