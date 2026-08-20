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
        Assert.Null(cfg.Unrecognized);         // nothing declared, nothing to report
        Assert.NotEmpty(cfg.Classifications);  // defaults present
        Assert.NotEmpty(cfg.Weights);          // defaults present
    }




    [Fact]
    public void LoadOrDefault_OverrideKeys_MatchTheirTargetsCaseInsensitively()
    {
        // System.Text.Json assigns new dictionaries through the setters, dropping the
        // OrdinalIgnoreCase comparers from the field initializers.
        var dir = Path.Combine(Path.GetTempPath(), "reforge-surface-score-test-case");
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        Directory.CreateDirectory(dir);
        var configPath = Path.Combine(dir, "reforge.surface-score.json");
        File.WriteAllText(configPath, """
            {
              "weights": { "CROSSSECTIONFULLSERVICE": 7 },
              "classifications": { "CONTROLLER": { "namePatterns": ["Zzz$"] } }
            }
            """);

        var cfg = SurfaceScoreConfig.LoadOrDefault(configPath, dir, out _);

        Assert.Equal(7, cfg.Weight("crossSectionFullService"));
        // The case-variant override must REPLACE the default, not sit beside it as a second entry.
        Assert.Single(cfg.Classifications, c => string.Equals(c.Key, "controller", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void LoadOrDefault_RetiredKeys_AreReportedNotFatal()
    {
        // v0.22 described section membership with paths/namespaces/symbols; later versions kept a
        // policy-only `sections` block. Both are gone. A stale config must still load — and every
        // dropped key must be nameable, because they used to move the score.
        var dir = Path.Combine(Path.GetTempPath(), "reforge-surface-score-test-legacy");
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        Directory.CreateDirectory(dir);
        var configPath = Path.Combine(dir, "reforge.surface-score.json");
        File.WriteAllText(configPath, """
            {
              "groups": [ { "name": "Legacy", "match": { "paths": ["**/Legacy/**"] } } ],
              "resources": { "dbSets": { "ownerByName": { "Users": "Legacy" } } },
              "sections": { "Users": { "primaryInfoDto": "UserInfo" } }
            }
            """);

        var cfg = SurfaceScoreConfig.LoadOrDefault(configPath, dir, out _);

        var warning = cfg.UnreadConfigKeysWarning();
        Assert.NotNull(warning);
        Assert.Contains("sections", warning);
        Assert.Contains("groups", warning);
        Assert.Contains("resources", warning);
    }

    // ---------------- Engine behavior ----------------

    [Fact]
    public async Task Grouping_IsByAssembly_WithoutAnyConfig()
    {
        var report = await ScoreDefaultAsync();

        // One section per non-test assembly, ".Contracts" folded into its parent.
        Assert.Equal(
            new[] { "Camp", "Core", "Dorm", "Gate", "Lodge", "Reporting", "Services", "Tent", "Web" },
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

    // ---------------- Dead / unreadable config classifications ----------------

    [Fact]
    public async Task ScoreAsync_DeclaredClassificationMatchingNothing_IsReported()
    {
        // The failure this catches, seen in a real config: a classification block aimed at a
        // directory the solution was reorganized out of. The block classifies nothing, every rule
        // keyed to the tag reads zero, and the score is identical to a solution with no problem.
        var cfg = SurfaceScoreConfig.Default();
        cfg.Classifications["controller"] = new ClassificationRule
        {
            Paths = new() { "src/NoSuchProject/Controllers/**" },
            Namespaces = new() { "NoSuchProject.Controllers" }
        };
        cfg.DeclaredClassifications.Add("controller");

        var engine = new SurfaceScoreEngine(cfg, LocationHelper.GetSolutionDirectory(_fixture.Solution));
        var report = await engine.ScoreAsync(_fixture.Solution, CancellationToken.None);

        var diagnostic = Assert.Single(report.Diagnostics, d => d.Code == "dead-config-classification");
        Assert.Contains("controller", diagnostic.Message);
        // The message has to say the block REPLACED the defaults, because that is the part
        // nobody guesses: deleting the block is a fix, and narrowing it is not.
        Assert.Contains("REPLACES", diagnostic.Message);
        // And the consequence the diagnostic exists to explain: the rule keyed to the tag scores
        // nothing, which on its own is indistinguishable from a clean solution.
        Assert.False(report.ByRule.ContainsKey("controllerAction"));
    }

    [Fact]
    public async Task ScoreAsync_DeclaredClassificationThatMatches_IsNotReported()
    {
        var cfg = SurfaceScoreConfig.Default();
        cfg.Classifications["dto"] = new ClassificationRule { NamePatterns = new() { "*Info" } };
        cfg.DeclaredClassifications.Add("dto");

        var engine = new SurfaceScoreEngine(cfg, LocationHelper.GetSolutionDirectory(_fixture.Solution));
        var report = await engine.ScoreAsync(_fixture.Solution, CancellationToken.None);

        Assert.DoesNotContain(report.Diagnostics, d => d.Code == "dead-config-classification");
    }

    [Fact]
    public async Task ScoreAsync_DefaultsThatMatchNothing_AreNotReported()
    {
        // Defaults are speculative by design — a solution with no controllers is not misconfigured,
        // and warning about every unmatched default would put noise in every run of every solution
        // that ships no config at all (including this tool's own dogfood run).
        var report = await ScoreDefaultAsync();

        Assert.DoesNotContain(report.Diagnostics, d => d.Code == "dead-config-classification");
    }

    [Fact]
    public async Task ScoreAsync_ClassificationKeyNoRuleReads_IsReportedSeparately()
    {
        // A typo'd key matches plenty and is still inert, so the dead-classification check cannot
        // see it. Two different mistakes, two different fixes, two diagnostics.
        var cfg = SurfaceScoreConfig.Default();
        cfg.Classifications["dtos"] = new ClassificationRule { NamePatterns = new() { "*Info" } };
        cfg.DeclaredClassifications.Add("dtos");

        var engine = new SurfaceScoreEngine(cfg, LocationHelper.GetSolutionDirectory(_fixture.Solution));
        var report = await engine.ScoreAsync(_fixture.Solution, CancellationToken.None);

        var diagnostic = Assert.Single(report.Diagnostics, d => d.Code == "unknown-config-classification");
        Assert.Contains("dtos", diagnostic.Message);
        Assert.Contains("controller", diagnostic.Message);  // the readable set is listed, so the fix is obvious
        Assert.DoesNotContain(report.Diagnostics, d => d.Code == "dead-config-classification");
    }

    [Fact]
    public async Task ScoreAsync_WeightForARetiredRule_IsReported()
    {
        // What retiring a rule leaves behind in a config that tuned it: a number that reads like
        // policy and is scored by nothing. genericActionDispatcher is the real case — it was a rule
        // until it was folded into actionDispatcher as surcharges.
        var cfg = SurfaceScoreConfig.Default();
        cfg.Weights["genericActionDispatcher"] = 3;

        var engine = new SurfaceScoreEngine(cfg, LocationHelper.GetSolutionDirectory(_fixture.Solution));
        var report = await engine.ScoreAsync(_fixture.Solution, CancellationToken.None);

        var diagnostic = Assert.Single(report.Diagnostics, d => d.Code == "unknown-config-weight");
        Assert.Contains("genericActionDispatcher", diagnostic.Message);
    }

    [Fact]
    public async Task ScoreAsync_DefaultWeights_AreNotReportedAsUnknown()
    {
        // Every default weight key must be a rule the engine reads, or the diagnostic above fires on
        // a solution with no config at all. This is the assertion that keeps the two lists in step.
        var report = await ScoreDefaultAsync();

        Assert.DoesNotContain(report.Diagnostics, d => d.Code == "unknown-config-weight");
    }

    [Fact]
    public void LoadOrDefault_RecordsWhichClassificationsCameFromTheFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), "reforge-surface-score-test-declared-classifications");
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        Directory.CreateDirectory(dir);
        var configPath = Path.Combine(dir, "reforge.surface-score.json");
        File.WriteAllText(configPath, """
            {
              "classifications": {
                "controller": { "paths": ["src/NoSuchProject/Controllers/**"] }
              }
            }
            """);

        var cfg = SurfaceScoreConfig.LoadOrDefault(configPath, dir, out _);

        Assert.Equal(new[] { "controller" }, cfg.DeclaredClassifications);
        // Merged defaults are not "declared" — otherwise every unmatched default would warn.
        Assert.True(cfg.Classifications.ContainsKey("dto"));
        Assert.DoesNotContain("dto", cfg.DeclaredClassifications);
        // And the declared block REPLACED the default controller patterns rather than extending
        // them. This is the whole reason the diagnostic is worth having.
        Assert.Empty(cfg.Classifications["controller"].NamePatterns);
        Assert.Empty(cfg.Classifications["controller"].Inherits);
    }

    [Fact]
    public void KnownClassifications_IsExactlyTheDefaultKeySet()
    {
        // The known set is derived from the defaults rather than hand-listed, so it cannot drift
        // from them. This pins the other half: the defaults are also exactly what the rules read.
        Assert.Equal(
            SurfaceScoreConfig.Default().Classifications.Keys.OrderBy(k => k, StringComparer.Ordinal),
            SurfaceScoreConfig.KnownClassifications.OrderBy(k => k, StringComparer.Ordinal));
    }

    // ---------------- Contracts-assembly multiplier ----------------

    [Fact]
    public async Task ContractsAssembly_SurfaceCharges_AreDoubled()
    {
        // SampleSolution.Camp.Contracts is a satellite contracts assembly; SampleSolution.Camp is
        // the section's own. Both fold into section "Camp", so the origin is the only thing that
        // separates them — and a charge on a declaration in the satellite costs twice as much.
        var report = await ScoreDefaultAsync();
        var camp = report.Groups["Camp"];

        // Positive surface charges only — the two exclusions are asserted separately below.
        var contracts = camp.Entries
            .Where(e => e.Origin == ScoreOrigin.Contracts && e.Points > 0
                        && !SurfaceScoreRuleGroups.IsInternalComplexity(e.Rule))
            .ToList();
        Assert.NotEmpty(contracts);
        Assert.All(contracts, e => Assert.True(e.Multiplied, $"{e.Rule} on {e.Symbol} was not scaled"));

        // Every doubled charge is even, because it is 2x an integer weight. Weak on its own, but
        // it fails loudly if the multiplier is ever applied to an already-scaled value.
        Assert.All(contracts, e => Assert.Equal(0, e.Points % 2));

        // The split is reported, not just folded into the surface total.
        Assert.Equal(camp.SurfaceTotal, camp.MainSurfaceTotal + camp.ContractsSurfaceTotal);
        Assert.True(camp.ContractsSurfaceTotal > 0);
    }

    [Fact]
    public async Task ContractsMultiplier_ScalesOnlyTheSatelliteAssembly_NotAContractsFolder()
    {
        // The distinction the multiplier turns on, within one section. CampInfo is declared in
        // SampleSolution.Camp.Contracts (satellite) and is scaled; ICampSectionService is declared
        // in SampleSolution.Camp (the section's own assembly) and is not. Both fold into "Camp".
        var report = await ScoreDefaultAsync();

        // Partitioned by declaring file rather than by symbol name: most entries are keyed to a
        // member, so the type that carries them is not what `Symbol` says.
        var camp = report.Groups["Camp"].Entries.Where(e => !string.IsNullOrEmpty(e.File)).ToList();
        var published = camp.Where(e => e.File.Contains("Camp.Contracts", StringComparison.Ordinal)).ToList();
        var inAssembly = camp.Where(e => !e.File.Contains("Camp.Contracts", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(published);
        Assert.NotEmpty(inAssembly);

        Assert.All(published, e => Assert.Equal(ScoreOrigin.Contracts, e.Origin));
        Assert.All(inAssembly, e => Assert.Equal(ScoreOrigin.Main, e.Origin));
        Assert.All(inAssembly, e => Assert.False(e.Multiplied));

        // Exactly 2x, not "some larger number" — the assertions that pin the factor. Two rules,
        // because a single one could pass on a coincidence between weight and multiplier.
        var defaults = SurfaceScoreConfig.Default();
        var dtoType = Assert.Single(published, e => e.Rule == "publicDtoType" && e.Symbol == "CampInfo");
        Assert.Equal(2 * defaults.Weight("publicDtoType"), dtoType.Points);

        var readMethod = Assert.Single(published,
            e => e.Rule == "readServiceInterfaceMethod" && e.Symbol == "GetByIdAsync");
        Assert.Equal(2 * defaults.Weight("readServiceInterfaceMethod"), readMethod.Points);
    }

    [Fact]
    public async Task ContractsMultiplier_OfOne_LeavesEveryChargeAlone()
    {
        var scaled = await ScoreDefaultAsync();
        var cfg = SurfaceScoreConfig.Default();
        cfg.ContractsAssemblyMultiplier = 1;
        var engine = new SurfaceScoreEngine(cfg, LocationHelper.GetSolutionDirectory(_fixture.Solution));
        var flat = await engine.ScoreAsync(_fixture.Solution, CancellationToken.None);

        Assert.DoesNotContain(AllEntries(flat), e => e.Multiplied);
        Assert.True(flat.SurfaceTotal < scaled.SurfaceTotal,
            "turning the multiplier off must lower the surface total, or nothing was being scaled");
        // Origin is still recorded — it describes where the symbol lives, not whether it was scaled.
        Assert.Contains(AllEntries(flat), e => e.Origin == ScoreOrigin.Contracts);
    }

    [Fact]
    public async Task ContractsMultiplier_DoesNotScaleCreditsOrInternalComplexity()
    {
        var report = await ScoreDefaultAsync();

        // Doubling a credit would make publishing pay, which inverts the rule's whole point.
        Assert.All(AllEntries(report).Where(e => e.Points < 0), e => Assert.False(e.Multiplied));
        // The internal axis is the counterweight to surface and has to keep one unit everywhere.
        Assert.All(AllEntries(report).Where(e => SurfaceScoreRuleGroups.IsInternalComplexity(e.Rule)),
            e => Assert.False(e.Multiplied));
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
        // CampLegacyStay is public and exported, but declared in SampleSolution.Camp with no
        // Contracts/ folder above it. Camp never published it, so returning it earns nothing.
        var report = await ScoreDefaultAsync();

        Assert.DoesNotContain(report.Groups["Reporting"].Entries, e => e.Rule == "canonicalReadDtoReturn"
            && e.Detail?.Contains("CampLegacyStay", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task RemovedSectionsBlock_IsReportedNotSilentlyIgnored()
    {
        // The block is gone, and System.Text.Json would drop it without a word — but a config that
        // still carries it used to anchor DTOs, override surface expectations, and suppress
        // penalties for whole sections.
        var dir = Path.Combine(Path.GetTempPath(), "reforge-surface-score-test-removed-field");
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        Directory.CreateDirectory(dir);
        var configPath = Path.Combine(dir, "reforge.surface-score.json");
        File.WriteAllText(configPath, """
            { "sections": { "Camp": { "canonicalReadDtos": ["CampInfo"] } } }
            """);

        var cfg = SurfaceScoreConfig.LoadOrDefault(configPath, dir, out _);

        var engine = new SurfaceScoreEngine(cfg, LocationHelper.GetSolutionDirectory(_fixture.Solution));
        var report = await engine.ScoreAsync(_fixture.Solution, CancellationToken.None);

        var diagnostic = Assert.Single(report.Diagnostics, d => d.Code == "removed-config-field");
        Assert.Contains("sections", diagnostic.Message);
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
        // ReadOnlyGreetingConsumer sits in another assembly and is charged by the same rule: the
        // section boundary is priced by the crossSection* rules, not by a second read-only rule.
        var report = await ScoreDefaultAsync();

        var readOnly = AllEntries(report).Where(e => e.Rule == "writeCapableInterfaceUsedReadOnly").ToList();
        Assert.Contains(readOnly, e => e.Symbol == "SameSectionGreetingConsumer");
        Assert.DoesNotContain(readOnly, e => e.Symbol == "FullGreetingConsumer");
        Assert.Contains(readOnly, e => e.Symbol == "ReadOnlyGreetingConsumer");
    }

    [Fact]
    public async Task WriteCapableUsedReadOnly_CountsAPartialClassAsOneConsumer()
    {
        // Both fixtures are split across two files, which must not change what they cost.
        var report = await ScoreDefaultAsync();
        var readOnly = AllEntries(report).Where(e => e.Rule == "writeCapableInterfaceUsedReadOnly").ToList();

        // Read-only in both halves: charged once, not once per declaration.
        Assert.Single(readOnly.Where(e => e.Symbol == "PartialReadOnlyGreetingConsumer"));
        // Write call in the other half: cancels the rule for the whole class, not just that file.
        Assert.DoesNotContain(readOnly, e => e.Symbol == "SplitWriteGreetingConsumer");
    }

    // ---------------- Internal complexity: dispatcher / read-shape ----------------

    [Fact]
    public async Task ActionDispatcher_FiresOnImplAndInterface()
    {
        // SignupWorkflowService.ApplyAsync: generic verb + SignupAction enum + switch that
        // delegates arms to distinct members. A structural dispatcher declared on an interface is
        // a contractual smell, so it must be attributed to the interface method as well.
        var report = await ScoreDefaultAsync();
        var hits = AllEntries(report).Where(e => e.Rule == "actionDispatcher" && e.Symbol == "ApplyAsync").ToList();
        Assert.True(hits.Count >= 2, $"expected actionDispatcher on both impl and interface ApplyAsync; got {hits.Count}");
        // The rule that used to own this shape is gone, folded in as surcharges.
        Assert.False(report.ByRule.ContainsKey("genericActionDispatcher"));
    }

    [Fact]
    public async Task ActionDispatcher_SurchargesTheGenericVerbAndTypedSelector()
    {
        // Same arm count (3), same structural dispatch, both mutations. ApplyAsync earns the
        // generic-verb surcharge; RouteAsync does not. Both carry an enum selector. So the two
        // must differ by exactly the generic-verb surcharge — the assertion that keeps the
        // surcharges graduated instead of collapsing back into a single flat price.
        var report = await ScoreDefaultAsync();
        var apply = AllEntries(report)
            .Where(e => e.Rule == "actionDispatcher" && e.Symbol == "ApplyAsync")
            .Max(e => e.Points);
        var route = Assert.Single(AllEntries(report),
            e => e.Rule == "actionDispatcher" && e.Symbol == "RouteAsync").Points;
        Assert.True(apply > route, $"ApplyAsync ({apply}) must outprice RouteAsync ({route})");
    }

    [Fact]
    public async Task MutationModeParameter_FiresOnInlineModeMutation()
    {
        // ThingService.CreateThingAsync: generic verb + mode enum but inline (no delegation).
        var report = await ScoreDefaultAsync();
        var entries = AllEntries(report).ToList();
        Assert.Contains(entries, e => e.Rule == "mutationModeParameter" && e.Symbol == "CreateThingAsync");
        Assert.DoesNotContain(entries, e => e.Rule == "actionDispatcher" && e.Symbol == "CreateThingAsync");
    }

    [Fact]
    public async Task StateEngine_IsExemptFromAllDispatcherRules()
    {
        // WorkflowService.ApplyWorkflowTransitionAsync switches on an action enum but validates
        // current state and uses transition vocabulary — a real state machine, exempt.
        var report = await ScoreDefaultAsync();
        var entries = AllEntries(report).Where(e => e.Symbol == "ApplyWorkflowTransitionAsync").ToList();
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
    public async Task GodMethod_FiresCognitiveComplexity()
    {
        var report = await ScoreDefaultAsync();
        var entries = AllEntries(report).ToList();
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
        // Bad consolidation: surface dropped 48 but complexity rose 63, driven by cognitiveComplexity
        // (a god-method growth, not a dispatcher) — so the kind is complexity-traded-for-surface.
        var basePath = WriteBaseline(surface: 1000, internalC: 50, byRule: new() { ["applicationServiceMethod"] = 80 });
        var now = MakeReport(surface: 952, internalC: 113, byRule: new() { ["applicationServiceMethod"] = 80, ["cognitiveComplexity"] = 63 });

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
        now.ByRule["actionDispatcher"] = 40;
        var g = new GroupScore { Name = "Shifts", SurfaceTotal = 220, InternalComplexityTotal = 60 };
        g.ByRule["actionDispatcher"] = 40;
        g.Entries.Add(new ScoreEntry("actionDispatcher", 40, "ApplySignupActionAsync", "Shifts", "ShiftSignupService.cs", 42, "ApplySignupActionAsync (3-arm dispatch on action)"));
        now.Groups["Shifts"] = g;

        var cmp = SurfaceScoreBaseline.Compare(now, basePath);

        Assert.NotEmpty(cmp.Suspicious);
        Assert.Contains(cmp.Suspicious, s => s.Message.Contains("ApplySignupActionAsync", StringComparison.Ordinal));
        Assert.Contains(cmp.Suspicious, s => s.Scope == "Shifts" && s.Kind == "generic-dispatcher-consolidation");
    }

    [Fact]
    public void Pareto_MethodSurfaceDownDispatcherUp_FlagsConsolidation()
    {
        // The sneaky non-traded case: complexity net IMPROVES (cognitiveComplexity fell more than the
        // dispatcher rose), so the verdict isn't "traded" — but a dispatcher appeared while
        // public method surface shrank. The early-warning detector must still flag it.
        var basePath = WriteBaseline(surface: 1000, internalC: 50, byRule: new() { ["applicationServiceMethod"] = 60, ["cognitiveComplexity"] = 50 });
        var now = MakeReport(surface: 985, internalC: 40, byRule: new() { ["applicationServiceMethod"] = 30, ["actionDispatcher"] = 30, ["cognitiveComplexity"] = 10 });

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
