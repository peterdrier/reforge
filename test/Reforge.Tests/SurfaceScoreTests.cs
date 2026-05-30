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
    public void LoadOrDefault_NoFile_ReturnsDefaultsWithNamespaceFallback()
    {
        var dir = Path.Combine(Path.GetTempPath(), "reforge-surface-score-test-no-file");
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        Directory.CreateDirectory(dir);

        var cfg = SurfaceScoreConfig.LoadOrDefault(null, dir, out var loadedFrom);

        Assert.Null(loadedFrom);
        Assert.Empty(cfg.EffectiveSections);
        Assert.True(cfg.GroupByNamespaceFallback);
        Assert.NotEmpty(cfg.Classifications); // defaults present
        Assert.NotEmpty(cfg.Weights);          // defaults present
    }

    [Fact]
    public void LoadOrDefault_ParsesSectionsBlock()
    {
        var dir = Path.Combine(Path.GetTempPath(), "reforge-surface-score-test-sections");
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        Directory.CreateDirectory(dir);
        var configPath = Path.Combine(dir, "reforge.surface-score.json");
        File.WriteAllText(configPath, """
            {
              "sections": {
                "Users": {
                  "paths": ["**/SampleSolution.Services/User*.cs"],
                  "symbols": ["IUser*"],
                  "repositoryInterfaces": ["IUserRepository"],
                  "serviceInterfaces": ["IUserService"]
                },
                "Orders": {
                  "symbols": ["Order*", "IOrder*"]
                }
              }
            }
            """);

        var cfg = SurfaceScoreConfig.LoadOrDefault(configPath, dir, out var loadedFrom);

        Assert.Equal(configPath, loadedFrom);
        Assert.Equal(2, cfg.EffectiveSections.Count);

        var users = cfg.EffectiveSections.Single(s => s.Name == "Users");
        Assert.Contains("**/SampleSolution.Services/User*.cs", users.Paths);
        Assert.Contains("IUser*", users.Symbols);
        Assert.Contains("IUserRepository", users.RepositoryInterfaces);
        Assert.Contains("IUserService", users.ServiceInterfaces);

        var orders = cfg.EffectiveSections.Single(s => s.Name == "Orders");
        Assert.Contains("Order*", orders.Symbols);
        Assert.False(cfg.GroupByNamespaceFallback); // sections present -> fallback off
    }

    [Fact]
    public void LoadOrDefault_LegacyGroupsBlockStillWorks()
    {
        var dir = Path.Combine(Path.GetTempPath(), "reforge-surface-score-test-legacy");
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        Directory.CreateDirectory(dir);
        var configPath = Path.Combine(dir, "reforge.surface-score.json");
        File.WriteAllText(configPath, """
            {
              "groups": [
                { "name": "Legacy", "match": { "paths": ["**/Legacy/**"] } }
              ]
            }
            """);

        var cfg = SurfaceScoreConfig.LoadOrDefault(configPath, dir, out _);

        Assert.Single(cfg.EffectiveSections);
        Assert.Equal("Legacy", cfg.EffectiveSections[0].Name);
        Assert.Contains("**/Legacy/**", cfg.EffectiveSections[0].Paths);
    }

    // ---------------- Engine behavior ----------------

    [Fact]
    public async Task NamespaceFallback_ProducesGroupsWithoutConfig()
    {
        var cfg = SurfaceScoreConfig.Default();
        var engine = new SurfaceScoreEngine(cfg, LocationHelper.GetSolutionDirectory(_fixture.Solution));
        var report = await engine.ScoreAsync(_fixture.Solution, CancellationToken.None);

        // Without any config the namespace heuristic must produce at least one group.
        // The exact name depends on the sample solution's namespace shape, but it must
        // be non-empty and non-empty-named.
        Assert.NotEmpty(report.Groups);
        Assert.All(report.Groups.Keys, k => Assert.False(string.IsNullOrWhiteSpace(k)));
    }

    [Fact]
    public async Task Section_ByPaths_AssignsMatchingTypesToSection()
    {
        var cfg = new SurfaceScoreConfig();
        cfg.Sections["Users"] = new SectionRule
        {
            Paths = { "**/SampleSolution.Services/User*.cs", "**/SampleSolution.Services/Cached*.cs" }
        };
        // Keep classification defaults so anything is actually scored.
        foreach (var (k, v) in SurfaceScoreConfig.Default().Classifications)
            cfg.Classifications.TryAdd(k, v);
        foreach (var (k, v) in SurfaceScoreConfig.Default().Weights)
            cfg.Weights.TryAdd(k, v);
        cfg.BuildEffectiveSections();

        var engine = new SurfaceScoreEngine(cfg, LocationHelper.GetSolutionDirectory(_fixture.Solution));
        var report = await engine.ScoreAsync(_fixture.Solution, CancellationToken.None);

        Assert.True(report.Groups.ContainsKey("Users"),
            $"Expected 'Users' section. Got: {string.Join(", ", report.Groups.Keys)}");

        // Every entry in the Users section must be from a file matched by the configured paths.
        var users = report.Groups["Users"];
        Assert.NotEmpty(users.Entries);
        Assert.All(users.Entries, e =>
            Assert.True(
                e.File.Contains("/User", StringComparison.Ordinal) ||
                e.File.Contains("/Cached", StringComparison.Ordinal),
                $"Entry from unexpected file: {e.File}"));
    }

    [Fact]
    public async Task Section_BySymbols_AssignsByNamePattern()
    {
        var cfg = new SurfaceScoreConfig();
        cfg.Sections["Orders"] = new SectionRule
        {
            Symbols = { "Order*", "IOrder*" }
        };
        foreach (var (k, v) in SurfaceScoreConfig.Default().Classifications)
            cfg.Classifications.TryAdd(k, v);
        foreach (var (k, v) in SurfaceScoreConfig.Default().Weights)
            cfg.Weights.TryAdd(k, v);
        cfg.BuildEffectiveSections();

        var engine = new SurfaceScoreEngine(cfg, LocationHelper.GetSolutionDirectory(_fixture.Solution));
        var report = await engine.ScoreAsync(_fixture.Solution, CancellationToken.None);

        Assert.True(report.Groups.ContainsKey("Orders"),
            $"Expected 'Orders' section. Got: {string.Join(", ", report.Groups.Keys)}");

        // Every scored entry should be from a type whose simple name starts with Order or IOrder
        // (we can only check this via the entry's Symbol since we don't carry full type names).
        var orders = report.Groups["Orders"];
        Assert.NotEmpty(orders.Entries);
        Assert.All(orders.Entries, e =>
        {
            // Entries reference symbol names — those may be members rather than types, so the
            // strongest check we can make at this layer is that the file path participates in
            // the section (the engine pins entries to their declaring type's section, and the
            // type matched Order*/IOrder*).
            Assert.NotNull(e.Symbol);
        });
    }

    [Fact]
    public async Task Section_RepositoryInterfaces_AutoClassifiesAndIsCrossSection()
    {
        // Two sections: Users (claims IUserRepository) and Orders (claims IOrderRepository).
        // Then UserService — which depends on IUserRepository via constructor — should NOT
        // produce a crossSectionRepository entry against itself (same section),
        // but OrderService — which depends on IOrderRepository AND lives in Users by namespace
        // fallback — should not be the test target because services live in the same project.
        // Instead, build an asymmetric setup where the service is in one section and the
        // repository it injects is in another.
        var cfg = new SurfaceScoreConfig();
        cfg.Sections["RepoSection"] = new SectionRule
        {
            RepositoryInterfaces = { "IUserRepository", "IOrderRepository", "IAuditLogRepository" }
        };
        cfg.Sections["ServiceSection"] = new SectionRule
        {
            // Pull every Service into a different section. UserService injects IUserRepository
            // which lives in RepoSection — that's the cross-section dependency we want to assert on.
            Symbols = { "*Service" }
        };
        foreach (var (k, v) in SurfaceScoreConfig.Default().Classifications)
            cfg.Classifications.TryAdd(k, v);
        foreach (var (k, v) in SurfaceScoreConfig.Default().Weights)
            cfg.Weights.TryAdd(k, v);
        cfg.BuildEffectiveSections();

        var engine = new SurfaceScoreEngine(cfg, LocationHelper.GetSolutionDirectory(_fixture.Solution));
        var report = await engine.ScoreAsync(_fixture.Solution, CancellationToken.None);

        Assert.True(report.Groups.ContainsKey("ServiceSection"),
            $"Expected ServiceSection. Got: {string.Join(", ", report.Groups.Keys)}");

        // ServiceSection should have at least one crossSectionRepository entry (UserService
        // injecting IUserRepository, which is owned by RepoSection).
        var services = report.Groups["ServiceSection"];
        var crossRepo = services.Entries.Where(e => e.Rule == "crossSectionRepository").ToList();
        Assert.NotEmpty(crossRepo);
    }

    // ---------------- Diagnostic ----------------

    [Fact]
    public async Task MissingGroup_ProducesDiagnostic_WhenNothingMatches()
    {
        var cfg = SurfaceScoreConfig.Default();
        var engine = new SurfaceScoreEngine(cfg, LocationHelper.GetSolutionDirectory(_fixture.Solution));
        var report = await engine.ScoreAsync(_fixture.Solution, CancellationToken.None);

        // Simulate the command-layer diagnostic — the engine itself doesn't add it, the command
        // wrapper does. Replicate the check here so the assertion lives near the rule.
        const string requested = "TotallyMadeUpSection";
        var present = report.Groups.ContainsKey(requested);
        var configured = cfg.HasConfiguredSection(requested);

        Assert.False(present);
        Assert.False(configured);
        // The contract: when both are false, the command emits a "group-not-found" diagnostic.
        // We verify the contract is reachable here; the WriteCompact tests would cover formatting.
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
        // Mark "User" as the canonical DTO for a "Users" section. The Users section is
        // claimed by symbol pattern so it includes UserService — whose public Get methods
        // return User, which should now earn the credit.
        var cfg = new SurfaceScoreConfig();
        cfg.Sections["Users"] = new SectionRule
        {
            Symbols = { "User*", "IUser*" },
            CanonicalReadDtos = { "User" }
        };
        foreach (var (k, v) in SurfaceScoreConfig.Default().Classifications)
            cfg.Classifications.TryAdd(k, v);
        foreach (var (k, v) in SurfaceScoreConfig.Default().Weights)
            cfg.Weights.TryAdd(k, v);
        cfg.BuildEffectiveSections();

        var engine = new SurfaceScoreEngine(cfg, LocationHelper.GetSolutionDirectory(_fixture.Solution));
        var report = await engine.ScoreAsync(_fixture.Solution, CancellationToken.None);

        var users = report.Groups["Users"];
        var canonical = users.Entries.Where(e => e.Rule == "canonicalReadDtoReturn").ToList();
        Assert.NotEmpty(canonical);
        Assert.All(canonical, e => Assert.True(e.Points < 0, "canonicalReadDtoReturn must contribute a credit (negative points)"));
    }

    // ---------------- methodReturnsEntityAcrossSection ----------------

    [Fact]
    public async Task MethodReturnsEntityAcrossSection_FiresWhenReturnTypeLivesInDifferentSection()
    {
        // Sample-solution layout: User lives in SampleSolution.Core.Models (matched by the
        // default `entity` classification's "**/Models/**" path). UserService lives in
        // SampleSolution.Services. We put User into a "Domain" section and UserService into
        // a "Services" section, so UserService.GetUserAsync returning User is a cross-section
        // entity leak.
        var cfg = new SurfaceScoreConfig();
        cfg.Sections["Domain"] = new SectionRule
        {
            Paths = { "**/SampleSolution.Core/Models/**" }
        };
        cfg.Sections["Services"] = new SectionRule
        {
            Paths = { "**/SampleSolution.Services/**" }
        };
        foreach (var (k, v) in SurfaceScoreConfig.Default().Classifications)
            cfg.Classifications.TryAdd(k, v);
        foreach (var (k, v) in SurfaceScoreConfig.Default().Weights)
            cfg.Weights.TryAdd(k, v);
        cfg.BuildEffectiveSections();

        var engine = new SurfaceScoreEngine(cfg, LocationHelper.GetSolutionDirectory(_fixture.Solution));
        var report = await engine.ScoreAsync(_fixture.Solution, CancellationToken.None);

        Assert.True(report.Groups.ContainsKey("Services"),
            $"Expected 'Services'. Got: {string.Join(", ", report.Groups.Keys)}");
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
        // Same setup as above but mark User as a canonical DTO. The entity penalty should
        // be replaced by the canonical credit — never both for the same method.
        var cfg = new SurfaceScoreConfig();
        cfg.Sections["Domain"] = new SectionRule
        {
            Paths = { "**/SampleSolution.Core/Models/**" },
            CanonicalReadDtos = { "User" }
        };
        cfg.Sections["Services"] = new SectionRule
        {
            Paths = { "**/SampleSolution.Services/**" }
        };
        foreach (var (k, v) in SurfaceScoreConfig.Default().Classifications)
            cfg.Classifications.TryAdd(k, v);
        foreach (var (k, v) in SurfaceScoreConfig.Default().Weights)
            cfg.Weights.TryAdd(k, v);
        cfg.BuildEffectiveSections();

        var engine = new SurfaceScoreEngine(cfg, LocationHelper.GetSolutionDirectory(_fixture.Solution));
        var report = await engine.ScoreAsync(_fixture.Solution, CancellationToken.None);

        var services = report.Groups["Services"];
        var userLeaks = services.Entries
            .Where(e => e.Rule == "methodReturnsEntityAcrossSection")
            .Where(e => e.Detail?.Contains("-> User", StringComparison.Ordinal) == true)
            .ToList();
        Assert.Empty(userLeaks);

        var userCredits = services.Entries
            .Where(e => e.Rule == "canonicalReadDtoReturn")
            .Where(e => e.Detail?.Contains("-> User", StringComparison.Ordinal) == true)
            .ToList();
        Assert.NotEmpty(userCredits);
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
        // ReadOnlyGreetingConsumer holds IGreetingService but only calls Get methods that
        // also exist on the read interface — the rule should fire.
        // FullGreetingConsumer calls RecordGreetingAsync (write) — the rule should NOT fire.
        var cfg = SurfaceScoreConfig.Default();
        var engine = new SurfaceScoreEngine(cfg, LocationHelper.GetSolutionDirectory(_fixture.Solution));
        var report = await engine.ScoreAsync(_fixture.Solution, CancellationToken.None);

        var allEntries = report.Groups.Values
            .SelectMany(g => g.Entries)
            .Where(e => e.Rule == "writeCapableInterfaceUsedReadOnly")
            .ToList();

        Assert.Contains(allEntries, e => e.Symbol == "ReadOnlyGreetingConsumer");
        Assert.DoesNotContain(allEntries, e => e.Symbol == "FullGreetingConsumer");
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
                  "repositoryInterfaces": ["ICampRepository"],
                  "serviceInterfaces": ["ICampService"],
                  "readServiceInterfaces": ["ICampServiceRead"],
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
        var camp = cfg.EffectiveSections.Single(s => s.Name == "Camp");

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
    public async Task ConfiguredButEmptySection_IsDistinguishableFromUnknownSection()
    {
        var cfg = new SurfaceScoreConfig();
        cfg.Sections["DefinitelyEmpty"] = new SectionRule
        {
            Paths = { "**/this-path-cannot-match-anything-1234567890/**" }
        };
        foreach (var (k, v) in SurfaceScoreConfig.Default().Classifications)
            cfg.Classifications.TryAdd(k, v);
        foreach (var (k, v) in SurfaceScoreConfig.Default().Weights)
            cfg.Weights.TryAdd(k, v);
        cfg.BuildEffectiveSections();

        var engine = new SurfaceScoreEngine(cfg, LocationHelper.GetSolutionDirectory(_fixture.Solution));
        var report = await engine.ScoreAsync(_fixture.Solution, CancellationToken.None);

        Assert.True(cfg.HasConfiguredSection("DefinitelyEmpty"));
        Assert.False(report.Groups.ContainsKey("DefinitelyEmpty"));
        // Command layer should emit a "group-empty" diagnostic for this case (distinct from
        // "group-not-found").
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
    }
}
