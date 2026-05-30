# Section-Architecture Scoring - Plan B (section-shape + scored rules) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task, TDD, commit per task. Steps use checkbox (`- [ ]`) syntax for tracking. Implementation code blocks for the complex analyzers (escape analysis, section-shape, conservation anchors) are strong drafts; red-green is the source of truth - refine in execution, keep tests as the contract.

**Goal:** Teach `surface-score` to understand section architecture - emit five new surface-axis scored rules, a `conservationAnchors` block, and a new `reforge section-shape` view command - all driven by config, no domain literal in the engine.

**Architecture:** A shared `SectionShapeAnalyzer` consumes the Plan A `SolutionClassifier` output and resolves, per section: owned repositories, read/full service interfaces (via `BuildFullToReadPairs`), primary/settings/cache DTOs (config or convention/inference), documented read shards, cross-section read vs write-surface use (with an escape-analysis "unverified" state), missing surfaces (gated to repo-backed via `SectionFacts`), charged read methods (via `ReadSurface.Classify`), and zero-point advisories. `surface-score` uses the shapes to emit the five scored rules + `conservationAnchors`; the new `section-shape` command renders the full shape (including advisories + visible-debt suppressions).

**Tech Stack:** .NET 10, Roslyn, System.Text.Json, xUnit. Build: `dotnet build Reforge.slnx`. Test: `dotnet test Reforge.slnx`. Windows + Git Bash; never chain `cd x && cmd` (hook-blocked). Docs ASCII only.

**Spec:** `docs/superpowers/specs/2026-05-29-section-architecture-scoring-design.md` (Sections 1-3, 5, 6; Section 4 conservationAnchors emission - the gate logic itself is Plan C).

**Verified ground state (2026-05-30):** main @ f70f12c, 96/96 tests green, build clean. Plan A foundation in place: `SolutionClassifier.ClassifyAsync -> IReadOnlyList<ClassifiedType>`; `ClassifiedType(Type, Group, Tags, File, PrimaryLocation)` with `.Line`; `SectionFacts.For(rule, classifiedRepoSectionNames)`; `ReadSurface.Classify(IMethodSymbol, primaryInfoDto, settingsInfoDto)` + `ReadMethodKind` + `IsCharged`; `DtoInventory.Build(root, canonicalTypeNames, maxDepth=5)`; `SectionRule` has PrimaryInfoDto/SettingsInfoDto/CacheDto/ReadShards/RequiresReadSurface/RequiresWriteSurface/RequiresPrimaryInfoDto/GrandfatheredDependencies/EscapeHatchReadMethods/HasConfiguredRepository. `SurfaceScoreEngine.BuildFullToReadPairs(classified, typesByDisplay)` is `internal static`. Report model (top of SurfaceScoreEngine.cs): records `ScoreEntry(Rule,Points,Symbol,Group,File,Line,Detail)`, classes `GroupScore`, `ScoreReport`, records `ScoreDiagnostic`, `SuspiciousImprovement`. `SurfaceScoreRuleGroups.InternalComplexity` (in ImplementationComplexity.cs) lists the 7 complexity rules; everything else is surface axis via `AddEntry`.

---

## Key engine integration points (verified line anchors, approximate)

- `ScoreAsync` (SurfaceScoreEngine.cs ~L94): after `classified` is built and `typesByDisplay` exists, add the section-architecture pass + anchors build before `return report`.
- `ScoreDurableSurface` (~L146) calls `ScoreInterfaceMethods(c, "readServiceInterfaceMethod", report)` for read interfaces - the `readSurfaceProjectionMethod` surcharge piggybacks here OR runs as part of the new pass.
- `ScoreWriteCapableUsedReadOnlyAsync` (~L554) + `BuildFullToReadPairs` (~L661): `crossSectionWriteSurface` specializes the cross-section case here.
- `AddEntry` (~L1231) routes to surface vs internal axis via `SurfaceScoreRuleGroups.IsInternalComplexity`.
- JSON payload in `SurfaceScoreCommand.WriteJson` (~L560): add `conservationAnchors` as an additive top-level key.

---

## Data model (fix these types once; later tasks depend on them)

Create in `src/Reforge/SectionShapeAnalyzer.cs`:

```csharp
public sealed record DtoAnchor(string Display, string Section, string Role /* primaryInfoDto|settingsInfoDto|cacheDto */, IReadOnlyList<string> Paths);
public sealed record InterfaceAnchorMethod(string Name, string Returns);
public sealed record InterfaceAnchor(string Display, string Section, string Role /* readServiceInterface|fullServiceInterface */, IReadOnlyList<InterfaceAnchorMethod> Methods);
public sealed record ShardAnchor(string Name, string Purpose, IReadOnlyList<string> Methods);

public sealed record CrossSectionUse(string Caller, string CallerSection, string Dependency, string DependencySection, string? SuggestedReadInterface, IReadOnlyList<string> ObservedCalls);
public sealed record MissingSurface(string Section, string Rule /* missingReadSurface|missingWriteSurface|missingPrimaryInfoDto */, string Detail);
public sealed record ChargedReadMethod(string Interface, string Method, ReadMethodKind Kind, string Returns, bool EscapeHatch, string? EscapeHatchReason);
public sealed record DerivableReadMethod(string Interface, string Method, ReadMethodKind Kind, string TargetDto, string Hint);
public sealed record MissingInfoFact(string Fact, string TargetDto);
public sealed record CacheFactCandidate(string Method, string Fact, string CacheDto);

public sealed record SectionShape(
    string Name,
    SectionFacts Facts,
    IReadOnlyList<string> OwnedRepositoryInterfaces,
    IReadOnlyList<string> OwnedRepositoryImplementations,
    IReadOnlyList<string> FullServiceInterfaces,
    IReadOnlyList<string> ReadServiceInterfaces,
    DtoAnchor? PrimaryInfoDto,
    DtoAnchor? SettingsInfoDto,
    DtoAnchor? CacheDto,
    string CacheDtoProvenance,        // configured | default-primary | inferred:<decorator> | none
    IReadOnlyList<ShardAnchor> ReadShards,
    IReadOnlyList<CrossSectionUse> ReadSurfaceCallers,
    IReadOnlyList<CrossSectionUse> WriteSurfaceCallers,        // confident crossSectionWriteSurface candidates
    IReadOnlyList<CrossSectionUse> WriteSurfaceUnverified,     // escape-analysis advisory
    IReadOnlyList<MissingSurface> Missing,
    IReadOnlyList<GrandfatheredDependency> Grandfathered,
    IReadOnlyList<EscapeHatchReadMethod> EscapeHatches,
    IReadOnlyList<ChargedReadMethod> ChargedReadMethods,
    IReadOnlyList<DerivableReadMethod> DerivableReadMethods,
    IReadOnlyList<MissingInfoFact> MissingInfoFacts,
    IReadOnlyList<CacheFactCandidate> CacheFactCandidates);

public sealed record SectionArchitecture(
    IReadOnlyList<SectionShape> Sections,
    IReadOnlyList<DtoAnchor> DtoAnchors,
    IReadOnlyList<InterfaceAnchor> InterfaceAnchors,
    IReadOnlyList<ShardAnchor> ShardAnchors);
```

Resolution rules (used throughout):
- **primaryInfoDto** for a configured section = `rule.PrimaryInfoDto ?? rule.Name + "Info"`. For namespace-fallback groups (no `SectionRule`) it is `null` -> section-architecture rules and the projection surcharge are skipped (cannot distinguish primitive read from projection without an anchor). This keeps default-config behaviour unchanged.
- **settingsInfoDto** = `rule.SettingsInfoDto ?? rule.Name + "SettingsInfo"`.
- **cacheDto** = `rule.CacheDto` (provenance `configured`) else inferred from a caching decorator (provenance `inferred:<Type>`, Task 7) else `primaryInfoDto` (provenance `default-primary`) else null (`none`).
- A DtoAnchor's symbol is resolved by matching the resolved name against `classified` types whose simple `Type.Name` equals it AND that are reachable (any section) - prefer a type in the section, else any classified DTO type with that name.
- `canonicalTypeNames` for `DtoInventory.Build` = the set of all classified DTO simple names in the solution (so child DTOs like CampSeasonInfo are descended).

---

## Task 1: Weights + glossary + axis for the five new rules

**Files:**
- Modify: `src/Reforge/SurfaceScoreConfig.cs` (`Default()` Weights dict, ~L192-248)
- Modify: `src/Reforge/SurfaceScoreRuleGlossary.cs` (`Descriptions`, ~L18-63)
- Test: `test/Reforge.Tests/SurfaceScoreTests.cs`

- [ ] **Step 1: Failing test** - add to `SurfaceScoreTests.cs`:

```csharp
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
public void Glossary_HasFactualLinesForNewRules()
{
    foreach (var rule in new[] { "crossSectionWriteSurface", "missingReadSurface",
        "missingWriteSurface", "missingPrimaryInfoDto", "readSurfaceProjectionMethod" })
        Assert.True(SurfaceScoreRuleGlossary.Descriptions.ContainsKey(rule), $"missing glossary: {rule}");
    // none of the new rules are on the internal-complexity axis
    foreach (var rule in new[] { "crossSectionWriteSurface", "missingReadSurface",
        "missingWriteSurface", "missingPrimaryInfoDto", "readSurfaceProjectionMethod" })
        Assert.False(SurfaceScoreRuleGroups.IsInternalComplexity(rule));
}
```

- [ ] **Step 2: Run -> FAIL** (`dotnet test Reforge.slnx --filter "FullyQualifiedName~Default_HasSectionArchitectureWeights|FullyQualifiedName~Glossary_HasFactualLinesForNewRules"`)

- [ ] **Step 3: Implement.** In `Default()` Weights, after `["writeCapableInterfaceUsedReadOnly"] = 12,` add:

```csharp
                // Section architecture (surface axis)
                ["crossSectionWriteSurface"] = 15,
                ["missingReadSurface"] = 10,
                ["missingWriteSurface"] = 10,
                ["missingPrimaryInfoDto"] = 10,
                ["readSurfaceProjectionMethod"] = 4,
```

In `SurfaceScoreRuleGlossary.Descriptions`, add (factual - no "use "/"prefer "/"split " - the existing `RuleGlossary_DescriptionsAreFactualNotAdvisory` test guards this):

```csharp
        ["crossSectionWriteSurface"] = "A class in one section injects another section's write/full service interface but every observed call targets a method that also exists on that section's read interface.",
        ["missingReadSurface"] = "A repo-backed section has no read-only service interface.",
        ["missingWriteSurface"] = "A repo-backed section has no write/full service interface.",
        ["missingPrimaryInfoDto"] = "A repo-backed section has no DTO matching its primary Info DTO name.",
        ["readSurfaceProjectionMethod"] = "A read-service-interface method returns a projection, predicate, scalar fact, or composed view rather than the section's primary Info DTO.",
```

- [ ] **Step 4: Run -> PASS**, then full suite `dotnet test Reforge.slnx` -> all green.
- [ ] **Step 5: Commit** `feat(surface-score): weights + glossary for 5 section-architecture rules`

---

## Task 2: `SectionShapeAnalyzer` core (shape resolution; no advisory, no cache-inference, no usage)

Build the analyzer that resolves the structural shape of each configured section. Cross-section usage, advisories, and cache inference land in later tasks (return empty lists for now). This is unit-tested directly against the sample solution, like `SolutionClassifierTests`.

**Files:**
- Create: `src/Reforge/SectionShapeAnalyzer.cs` (records from the Data Model section + the analyzer)
- Modify: `test/SampleSolution/SampleSolution.Services/CampFixtures.cs` (add `ICampRepository` so Camp is repo-backed)
- Create: `test/Reforge.Tests/SectionShapeAnalyzerTests.cs`

- [ ] **Step 1: Fixture** - append to `CampFixtures.cs`:

```csharp
// Repository so the Camp section is repo-backed (drives requiresX defaults).
public interface ICampRepository
{
    Task<CampInfo?> FindAsync(Guid id, CancellationToken ct = default);
}
```

- [ ] **Step 2: Failing test** - create `SectionShapeAnalyzerTests.cs`:

```csharp
using Microsoft.CodeAnalysis;

namespace Reforge.Tests;

[Collection("SampleSolution")]
public class SectionShapeAnalyzerTests
{
    private readonly SampleSolutionFixture _fixture;
    public SectionShapeAnalyzerTests(SampleSolutionFixture fixture) => _fixture = fixture;

    private static SurfaceScoreConfig CampConfig()
    {
        var cfg = new SurfaceScoreConfig
        {
            Sections =
            {
                ["Camp"] = new SectionRule
                {
                    RepositoryInterfaces = { "ICampRepository" },
                    ServiceInterfaces = { "ICampSectionService" },
                    ReadServiceInterfaces = { "ICampServiceRead" }
                    // primaryInfoDto/settingsInfoDto left to convention -> CampInfo / CampSettingsInfo
                }
            }
        };
        cfg.BuildEffectiveSections();
        return cfg;
    }

    [Fact]
    public async Task Analyze_ResolvesCampShape()
    {
        var cfg = CampConfig();
        var dir = LocationHelper.GetSolutionDirectory(_fixture.Solution);
        var classified = (await SolutionClassifier.ClassifyAsync(_fixture.Solution, cfg, dir, CancellationToken.None)).ToList();
        var arch = await SectionShapeAnalyzer.AnalyzeAsync(_fixture.Solution, classified, cfg, dir, CancellationToken.None);
        var camp = arch.Sections.Single(s => s.Name == "Camp");

        Assert.True(camp.Facts.RepoBacked);
        Assert.Contains("ICampRepository", camp.OwnedRepositoryInterfaces);
        Assert.Contains("ICampServiceRead", camp.ReadServiceInterfaces);
        Assert.Contains("ICampSectionService", camp.FullServiceInterfaces);
        Assert.Equal("CampInfo", camp.PrimaryInfoDto!.Display.Split('.').Last());
        Assert.Equal("CampSettingsInfo", camp.SettingsInfoDto!.Display.Split('.').Last());
        // cache defaults to primary when unconfigured
        Assert.Equal("CampInfo", camp.CacheDto!.Display.Split('.').Last());
        Assert.Equal("default-primary", camp.CacheDtoProvenance);
        // recursive path inventory present on the primary anchor
        Assert.Contains(camp.PrimaryInfoDto.Paths, p => p == "CampInfo.Seasons[].Members[].UserId");
        // charged read methods: predicate + projection; healthy: GetById (primary), GetSettings (settings)
        Assert.Contains(camp.ChargedReadMethods, m => m.Method == "IsUserCampLeadAsync" && m.Kind == ReadMethodKind.Predicate);
        Assert.Contains(camp.ChargedReadMethods, m => m.Method == "GetCampSummariesForYearAsync" && m.Kind == ReadMethodKind.ProjectionSummary);
        Assert.DoesNotContain(camp.ChargedReadMethods, m => m.Method == "GetByIdAsync");
        Assert.DoesNotContain(camp.ChargedReadMethods, m => m.Method == "GetSettingsAsync");
        // no missing surfaces: Camp has read+write+CampInfo
        Assert.Empty(camp.Missing);
    }
}
```

- [ ] **Step 3: Implement `SectionShapeAnalyzer`.** Algorithm:
  1. Group `classified` by `.Group`. For each `SectionRule` in `config.EffectiveSections`, gather its section's classified types (Group == rule.Name).
  2. `OwnedRepositoryInterfaces` = section types tagged `repositoryInterface`; `OwnedRepositoryImplementations` = tagged `repositoryImplementation`; `ReadServiceInterfaces` = tagged `readServiceInterface`; `FullServiceInterfaces` = tagged `fullServiceInterface`.
  3. `classifiedRepoSectionNames` = set of Groups that contain any `repositoryInterface` or `repositoryImplementation`. `Facts = SectionFacts.For(rule, classifiedRepoSectionNames)`.
  4. Resolve primary/settings names by convention (above). Find the symbol: a classified type whose `Type.Name == name` (prefer same Group). `canonicalTypeNames` = all classified DTO-tagged simple names. `Paths = DtoInventory.Build(symbol, canonicalTypeNames)`. Build `DtoAnchor(symbol.ToDisplayString(), section, role, paths)`. Cache = configured? -> anchor(role cacheDto) ; else default to primary anchor (role cacheDto, provenance default-primary).
  5. `ChargedReadMethods`: for each read-service interface in the section, for each ordinary method, `kind = ReadSurface.Classify(m, primaryName, settingsName)`; if `ReadSurface.IsCharged(kind)` add `ChargedReadMethod` (EscapeHatch via glob match over `rule.EscapeHatchReadMethods` on `"{Interface}.{Method}"` or `"{Method}"`; charged-but-escape-hatch still listed, flagged).
  6. `Missing`: if `Facts.RequiresReadSurface` and no read interface -> missingReadSurface; `RequiresWriteSurface` and no full interface -> missingWriteSurface; `RequiresPrimaryInfoDto` and primary symbol not found -> missingPrimaryInfoDto.
  7. Cross-section/advisory/cache-inference: empty for now.
  8. Build `SectionArchitecture` aggregating DtoAnchors (primary/settings/cache, deduped by Display+Role) and InterfaceAnchors (read+full, each with `{name, returns}` from ordinary methods) and ShardAnchors (from `rule.ReadShards`).

  `AnalyzeAsync` signature:
  ```csharp
  public static Task<SectionArchitecture> AnalyzeAsync(Solution solution,
      List<ClassifiedType> classified, SurfaceScoreConfig config, string solutionDirectory, CancellationToken ct)
  ```
  (Async for Task 5/7 which need semantic models; for now wrap a synchronous build in `Task.FromResult`.)

  Helper: `Returns` string for an interface method = `m.ReturnType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)`.

- [ ] **Step 4: Run -> PASS** (filter `SectionShapeAnalyzerTests`), then full suite green. New `ICampRepository` may add surface points to the namespace-fallback "Services" group in default-config tests, but existing tests assert behaviorally per-rule/per-group; if any hard count breaks, update it and note in commit.
- [ ] **Step 5: Commit** `feat(surface-score): SectionShapeAnalyzer resolves section shape + anchors`

---

## Task 3: `readSurfaceProjectionMethod` surcharge (scored)

Wire the analyzer's `ChargedReadMethods` into the engine as a scored surcharge on the surface axis. Only for sections with a resolved primaryInfoDto (configured sections); escape-hatch methods are exempt but recorded.

**Files:**
- Modify: `src/Reforge/SurfaceScoreEngine.cs` (`ScoreAsync` + new `ScoreSectionArchitecture`)
- Test: `test/Reforge.Tests/SurfaceScoreTests.cs`

- [ ] **Step 1: Failing test** (uses the Camp config helper; replicate the `CampConfig()` builder inline or add a shared test helper):

```csharp
[Fact]
public async Task ReadSurfaceProjectionMethod_ChargesProjectionAndPredicateReads()
{
    var cfg = new SurfaceScoreConfig { Sections = { ["Camp"] = new SectionRule {
        RepositoryInterfaces = { "ICampRepository" },
        ServiceInterfaces = { "ICampSectionService" },
        ReadServiceInterfaces = { "ICampServiceRead" } } } };
    cfg.BuildEffectiveSections();
    var engine = new SurfaceScoreEngine(cfg, LocationHelper.GetSolutionDirectory(_fixture.Solution));
    var report = await engine.ScoreAsync(_fixture.Solution, CancellationToken.None);

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
    var cfg = new SurfaceScoreConfig { Sections = { ["Camp"] = new SectionRule {
        RepositoryInterfaces = { "ICampRepository" },
        ServiceInterfaces = { "ICampSectionService" },
        ReadServiceInterfaces = { "ICampServiceRead" },
        EscapeHatchReadMethods = { new EscapeHatchReadMethod { Method = "ICampServiceRead.IsUserCampLeadAsync", Reason = "legacy", Since = "2026-02" } } } } };
    cfg.BuildEffectiveSections();
    var engine = new SurfaceScoreEngine(cfg, LocationHelper.GetSolutionDirectory(_fixture.Solution));
    var report = await engine.ScoreAsync(_fixture.Solution, CancellationToken.None);
    // only the projection method remains charged -> 4
    Assert.Equal(4, report.Groups["Camp"].ByRule["readSurfaceProjectionMethod"]);
}
```

- [ ] **Step 2: Run -> FAIL.**
- [ ] **Step 3: Implement.** In `ScoreAsync`, after `typesByDisplay` is built and before `return report` (after the other passes), add:

```csharp
        // Section architecture (Plan B): shapes drive the five new surface-axis rules + anchors.
        var architecture = await SectionShapeAnalyzer.AnalyzeAsync(solution, classified, _config, _solutionDirectory, ct);
        ScoreSectionArchitecture(architecture, report);
        report.ConservationAnchors = BuildConservationAnchors(architecture, report); // Task 6 (return [] until then)
```

New method:
```csharp
    private void ScoreSectionArchitecture(SectionArchitecture arch, ScoreReport report)
    {
        var projW = _config.Weight("readSurfaceProjectionMethod");
        foreach (var section in arch.Sections)
        {
            if (projW != 0 && section.PrimaryInfoDto is not null)
                foreach (var rm in section.ChargedReadMethods)
                {
                    if (rm.EscapeHatch) continue;
                    AddEntry(report, section.Name, "readSurfaceProjectionMethod", projW,
                        rm.Interface, rm.Method, /* file/line resolved from the interface method - see below */ ...);
                }
            // missing* (Task 4) and crossSectionWriteSurface (Task 5) added here too.
        }
    }
```

Note: `AddEntry` takes `ISymbol symbol`. The analyzer should carry the `IMethodSymbol` (or its file/line) on `ChargedReadMethod`. Simplest: add `string File, int Line` to `ChargedReadMethod` (resolved in the analyzer from the method's source location, normalized with `LocationHelper.NormalizePath`), and add an `AddEntry` overload (or reuse) that takes name/file/line directly. Match the existing private `AddEntry(report, group, rule, points, ISymbol, file, line, detail)` by carrying the `IMethodSymbol` on the record (it is in-memory only, never serialized). Detail = `$"{rm.Interface}.{rm.Method} ({rm.Kind})"`.

  - For the surcharge, also stub `BuildConservationAnchors` returning `new()` and add `public List<ConservationAnchor> ConservationAnchors { get; set; } = new();` to `ScoreReport` (full type in Task 6; for now a minimal placeholder record so it compiles). To avoid churn, introduce the real `ConservationAnchor` record now (Task 6 fills the builder).

- [ ] **Step 4: Run -> PASS** both new tests; full suite green.
- [ ] **Step 5: Commit** `feat(surface-score): readSurfaceProjectionMethod surcharge (section-aware)`

---

## Task 4: `missing*` rules (scored, repo-backed-gated)

**Files:**
- Modify: `src/Reforge/SurfaceScoreEngine.cs` (`ScoreSectionArchitecture`)
- Modify: `test/SampleSolution/SampleSolution.Services/CampFixtures.cs` (add the three deficient sections + an orchestrator)
- Test: `test/Reforge.Tests/SurfaceScoreTests.cs`

- [ ] **Step 1: Fixtures** - append to `CampFixtures.cs` (distinct simple names so they classify cleanly):

```csharp
// Repo-backed section missing a READ interface (has repo + full write service, no *ServiceRead).
public interface ILodgeRepository { Task<LodgeInfo?> FindAsync(Guid id, CancellationToken ct = default); }
public interface ILodgeService { Task RenameAsync(Guid id, string name, CancellationToken ct = default); }
public sealed class LodgeService : ILodgeService { public Task RenameAsync(Guid id, string name, CancellationToken ct = default) => Task.CompletedTask; }
public sealed class LodgeInfo { public Guid Id { get; set; } public string Name { get; set; } = ""; }

// Repo-backed section missing a WRITE/full interface (has repo + read, no full service).
public interface IDormRepository { Task<DormInfo?> FindAsync(Guid id, CancellationToken ct = default); }
public interface IDormServiceRead { Task<DormInfo> GetByIdAsync(Guid id, CancellationToken ct = default); }
public sealed class DormInfo { public Guid Id { get; set; } public string Name { get; set; } = ""; }

// Repo-backed section missing a primary Info DTO (has repo + read + full, no TentInfo).
public interface ITentRepository { Task FindAsync(Guid id, CancellationToken ct = default); }
public interface ITentServiceRead { Task<bool> ExistsAsync(Guid id, CancellationToken ct = default); }
public interface ITentService { Task PitchAsync(Guid id, CancellationToken ct = default); }
public sealed class TentService : ITentService { public Task PitchAsync(Guid id, CancellationToken ct = default) => Task.CompletedTask; }

// Orchestrator-only section (NO repository) - must NOT trip any missing* rule.
public interface IBookingOrchestrator { Task RunAsync(CancellationToken ct = default); }
public sealed class BookingOrchestrator : IBookingOrchestrator
{
    private readonly ICampSectionService _camp;
    public BookingOrchestrator(ICampSectionService camp) => _camp = camp;
    public Task RunAsync(CancellationToken ct = default) => Task.CompletedTask;
}
```

- [ ] **Step 2: Failing test**:

```csharp
[Fact]
public async Task MissingSurfaceRules_FireOnlyForRepoBackedSections()
{
    var cfg = new SurfaceScoreConfig { Sections = {
        ["Lodge"] = new SectionRule { RepositoryInterfaces = { "ILodgeRepository" }, ServiceInterfaces = { "ILodgeService" } },
        ["Dorm"]  = new SectionRule { RepositoryInterfaces = { "IDormRepository" }, ReadServiceInterfaces = { "IDormServiceRead" } },
        ["Tent"]  = new SectionRule { RepositoryInterfaces = { "ITentRepository" }, ServiceInterfaces = { "ITentService" }, ReadServiceInterfaces = { "ITentServiceRead" } },
        ["Booking"] = new SectionRule { Symbols = { "*Orchestrator" } }
    } };
    cfg.BuildEffectiveSections();
    var engine = new SurfaceScoreEngine(cfg, LocationHelper.GetSolutionDirectory(_fixture.Solution));
    var report = await engine.ScoreAsync(_fixture.Solution, CancellationToken.None);

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
```

- [ ] **Step 3: Implement** - in `ScoreSectionArchitecture`, after the projection loop, per section:

```csharp
            foreach (var miss in section.Missing)
            {
                var w = _config.Weight(miss.Rule);
                if (w == 0) continue;
                AddEntryByName(report, section.Name, miss.Rule, w, section.Name, /*file*/ "", /*line*/ 0, miss.Detail);
            }
```

The analyzer's Task-2 `Missing` computation already gates on `Facts.RequiresReadSurface/WriteSurface/PrimaryInfoDto`. Verify Lodge (has LodgeInfo by convention name) does not fire missingPrimaryInfoDto - convention name is "LodgeInfo", which exists -> primary resolves -> no missing. Good. (Introduce a tiny `AddEntryByName` helper that builds a `ScoreEntry` without an `ISymbol`, since missing* is section-level not symbol-level; or pass the section's first interface symbol. Keep symbol name = section name.)

- [ ] **Step 4: Run -> PASS**; full suite green.
- [ ] **Step 5: Commit** `feat(surface-score): missingReadSurface/missingWriteSurface/missingPrimaryInfoDto (repo-backed gated)`

---

## Task 5: `crossSectionWriteSurface` (scored) + `crossSectionWriteSurfaceUnverified` (advisory) + escape analysis

Specialize the cross-section case of `writeCapableInterfaceUsedReadOnly`. When a class in section A injects section B's full interface (paired with a read interface) and every observed call is read-covered AND the dependency does not escape analysis AND is not grandfathered: fire `crossSectionWriteSurface` (15) on A and suppress the generic `writeCapableInterfaceUsedReadOnly` for that dependency. If the dependency escapes (stored beyond its backing field, passed onward, returned, captured, reflected): emit `crossSectionWriteSurfaceUnverified` advisory (0 pts, on the shape; diagnostic too) instead of a confident penalty.

This is computed in the analyzer (`WriteSurfaceCallers` + `WriteSurfaceUnverified`) so both the engine and `section-shape` share it; the engine scores `WriteSurfaceCallers` and emits the advisory as a diagnostic.

**Files:**
- Modify: `src/Reforge/SectionShapeAnalyzer.cs` (usage + escape analysis)
- Modify: `src/Reforge/SurfaceScoreEngine.cs` (score `WriteSurfaceCallers`; suppress generic; advisory diagnostic)
- Modify: `test/SampleSolution/SampleSolution.Services/CampFixtures.cs` (cross-section read-only caller + passes-onward caller)
- Test: `test/Reforge.Tests/SurfaceScoreTests.cs`

- [ ] **Step 1: Fixtures** - append to `CampFixtures.cs`:

```csharp
// Cross-section caller that injects the Camp full interface but only READS -> crossSectionWriteSurface.
public sealed class CampReportBuilder
{
    private readonly ICampSectionService _camp;
    public CampReportBuilder(ICampSectionService camp) => _camp = camp;
    public async Task<string> BuildAsync(Guid id)
    {
        var info = await _camp.GetByIdAsync(id);   // read-covered (exists on ICampServiceRead)
        return info.Name;
    }
}

// Cross-section caller that PASSES the injected dependency onward -> unknown usage (advisory, not penalty).
public sealed class CampDelegator
{
    private readonly ICampSectionService _camp;
    public CampDelegator(ICampSectionService camp) => _camp = camp;
    public Task HandOffAsync() => Consume(_camp);            // escapes: passed as an argument
    private static Task Consume(ICampServiceRead svc) => Task.CompletedTask;
}
```

These callers must live in a DIFFERENT section than Camp. Put `CampReportBuilder`/`CampDelegator` in a configured "Reporting" section (Symbols `*ReportBuilder`,`*Delegator`) in the test config.

- [ ] **Step 2: Failing test**:

```csharp
[Fact]
public async Task CrossSectionWriteSurface_FiresOnCrossSectionReadOnlyConsumer_SuppressesGeneric()
{
    var cfg = new SurfaceScoreConfig { Sections = {
        ["Camp"] = new SectionRule { RepositoryInterfaces = { "ICampRepository" }, ServiceInterfaces = { "ICampSectionService" }, ReadServiceInterfaces = { "ICampServiceRead" } },
        ["Reporting"] = new SectionRule { Symbols = { "*ReportBuilder", "*Delegator" } }
    } };
    cfg.BuildEffectiveSections();
    var engine = new SurfaceScoreEngine(cfg, LocationHelper.GetSolutionDirectory(_fixture.Solution));
    var report = await engine.ScoreAsync(_fixture.Solution, CancellationToken.None);

    var reporting = report.Groups["Reporting"];
    Assert.True(reporting.ByRule.ContainsKey("crossSectionWriteSurface"));
    // generic writeCapableInterfaceUsedReadOnly is suppressed for that same dependency (CampReportBuilder)
    Assert.DoesNotContain(reporting.Entries, e => e.Rule == "writeCapableInterfaceUsedReadOnly" && e.Symbol == "CampReportBuilder");
}

[Fact]
public async Task CrossSectionWriteSurfaceUnverified_WhenDependencyEscapes_NoConfidentPenalty()
{
    var cfg = new SurfaceScoreConfig { Sections = {
        ["Camp"] = new SectionRule { RepositoryInterfaces = { "ICampRepository" }, ServiceInterfaces = { "ICampSectionService" }, ReadServiceInterfaces = { "ICampServiceRead" } },
        ["Reporting"] = new SectionRule { Symbols = { "*ReportBuilder", "*Delegator" } }
    } };
    cfg.BuildEffectiveSections();
    var engine = new SurfaceScoreEngine(cfg, LocationHelper.GetSolutionDirectory(_fixture.Solution));
    var report = await engine.ScoreAsync(_fixture.Solution, CancellationToken.None);

    // CampDelegator passes the dep onward -> NOT a confident crossSectionWriteSurface; an advisory diagnostic instead.
    var reporting = report.Groups["Reporting"];
    Assert.DoesNotContain(reporting.Entries, e => e.Rule == "crossSectionWriteSurface" && e.Symbol == "CampDelegator");
    Assert.Contains(report.Diagnostics, d => d.Code == "crossSectionWriteSurfaceUnverified" && d.Message.Contains("CampDelegator"));
}

[Fact]
public async Task CrossSectionWriteSurface_GrandfatheredDependency_IsSuppressed()
{
    var cfg = new SurfaceScoreConfig { Sections = {
        ["Camp"] = new SectionRule { RepositoryInterfaces = { "ICampRepository" }, ServiceInterfaces = { "ICampSectionService" }, ReadServiceInterfaces = { "ICampServiceRead" } },
        ["Reporting"] = new SectionRule { Symbols = { "*ReportBuilder", "*Delegator" },
            GrandfatheredDependencies = { new GrandfatheredDependency { Dependency = "CampReportBuilder->ICampSectionService", Reason = "legacy", Since = "2026-03", Owner = "camps" } } }
    } };
    cfg.BuildEffectiveSections();
    var engine = new SurfaceScoreEngine(cfg, LocationHelper.GetSolutionDirectory(_fixture.Solution));
    var report = await engine.ScoreAsync(_fixture.Solution, CancellationToken.None);
    Assert.DoesNotContain(report.Groups["Reporting"].Entries, e => e.Rule == "crossSectionWriteSurface" && e.Symbol == "CampReportBuilder");
}
```

- [ ] **Step 3: Implement.** In `SectionShapeAnalyzer.AnalyzeAsync` (now genuinely async), for each class consumer, for each ctor param whose type is a paired full interface owned by a *different* section:
  - Resolve the consumer's backing field for the param (a field assigned `field = param` in the ctor, matching type).
  - Walk the consumer's syntax (all declaring trees via semantic model). Classify the dependency reference:
    - **Invocation** `_field.Method(...)` / `param.Method(...)` -> record observed call; read-covered iff method name is on the read interface.
    - **Escape** if the field/param appears as: an argument to any invocation/object-creation (not as the receiver), a `return` expression, the RHS assigned to anything other than its own backing field, a captured identifier inside a lambda/local function, or an argument to `typeof`/reflection/`dynamic`. (Reuse `MemberAccessExpressionSyntax` receiver-type checks like Pass 5; for escape, look at `IdentifierNameSyntax`/`MemberAccessExpressionSyntax` nodes referring to the field/param whose parent is `ArgumentSyntax`, `ReturnStatementSyntax`, `AssignmentExpressionSyntax` (left != backing field), or inside a `LambdaExpressionSyntax`/`AnonymousMethodExpressionSyntax`/`LocalFunctionStatementSyntax`.)
  - Decision per dependency:
    - escapes -> `WriteSurfaceUnverified` entry.
    - else all observed calls read-covered AND at least one call AND not grandfathered -> `WriteSurfaceCallers` entry (SuggestedReadInterface = the paired read interface name).
    - else (a full-only call) -> neither (the generic rule will handle it).
  - Grandfathered match: `rule.GrandfatheredDependencies` of the consumer's section contains `Dependency` equal to `"{Caller}->{IFullInterface}"` or just `"{Caller}"`.

  In the engine `ScoreSectionArchitecture`:
  ```csharp
            var csW = _config.Weight("crossSectionWriteSurface");
            foreach (var use in section.WriteSurfaceCallers)
            {
                if (csW != 0)
                    AddEntryByName(report, section.Name, "crossSectionWriteSurface", csW, use.Caller, /*file/line of caller*/, 
                        $"{use.Caller} <- {use.Dependency} (use {use.SuggestedReadInterface}; cross-section, all reads)");
                suppressGeneric.Add((use.Caller, use.Dependency)); // tell Pass 5 to skip
            }
            foreach (var use in section.WriteSurfaceUnverified)
                report.Diagnostics.Add(new ScoreDiagnostic("info", "crossSectionWriteSurfaceUnverified",
                    $"{use.Caller} <- {use.Dependency}: read-only use unconfirmed (dependency escapes analysis); advisory only."));
  ```
  Suppression of the generic: the analyzer is the single source. Simplest robust approach: have the analyzer also drive which `(caller,dep)` pairs the generic Pass 5 must skip. Since both run in `ScoreAsync`, compute `architecture` BEFORE Pass 5 and pass a `HashSet<(string caller,string depDisplay)>` of confident cross-section pairs into `ScoreWriteCapableUsedReadOnlyAsync` to skip. (Move the architecture computation up, or split: compute shapes early, score them late.) Cleanest: compute `architecture` right after `typesByDisplay`; store the suppress-set on a local; pass it to Pass 5.

- [ ] **Step 4: Run -> PASS** all three; full suite green. Confirm existing `WriteCapableUsedReadOnly_FiresOnReadOnlyConsumerOfFullInterface` still passes (it uses a same-section or unconfigured consumer, so crossSectionWriteSurface does not fire and the generic still does - verify; if that test's consumer is cross-section under its config, adjust so the generic path remains exercised).
- [ ] **Step 5: Commit** `feat(surface-score): crossSectionWriteSurface + unverified advisory + escape analysis`

---

## Task 6: `conservationAnchors` emission (report + JSON)

**Files:**
- Modify: `src/Reforge/SurfaceScoreEngine.cs` (`ConservationAnchor` record + `BuildConservationAnchors` + `ScoreReport.ConservationAnchors`)
- Modify: `src/Reforge/Commands/SurfaceScoreCommand.cs` (`WriteJson` payload)
- Test: `test/Reforge.Tests/SurfaceScoreTests.cs` (report) + `SectionShapeAnalyzerTests` (anchors) + a JSON test

- [ ] **Step 1: Failing test** (report-level):

```csharp
[Fact]
public async Task ConservationAnchors_EmittedFqKeyedWithRecursivePaths()
{
    var cfg = new SurfaceScoreConfig { Sections = { ["Camp"] = new SectionRule {
        RepositoryInterfaces = { "ICampRepository" }, ServiceInterfaces = { "ICampSectionService" },
        ReadServiceInterfaces = { "ICampServiceRead" } } } };
    cfg.BuildEffectiveSections();
    var engine = new SurfaceScoreEngine(cfg, LocationHelper.GetSolutionDirectory(_fixture.Solution));
    var report = await engine.ScoreAsync(_fixture.Solution, CancellationToken.None);

    var primary = report.ConservationAnchors.Single(a => a.Key.EndsWith("CampInfo") && a.Role == "primaryInfoDto");
    Assert.Equal("Camp", primary.Section);
    Assert.Contains("CampInfo.Seasons[].Members[].UserId", primary.Paths);
    // interface anchor carries {name, returns}
    var read = report.ConservationAnchors.Single(a => a.Key.EndsWith("ICampServiceRead") && a.Role == "readServiceInterface");
    Assert.Contains(read.Methods, m => m.Name == "GetByIdAsync");
    // per-anchor byRule points exist for the read interface (readServiceInterfaceMethod + readSurfaceProjectionMethod)
    Assert.True(read.ByRule.ContainsKey("readServiceInterfaceMethod"));
}
```

- [ ] **Step 2: Run -> FAIL.**
- [ ] **Step 3: Implement.** Replace the Task-3 placeholder with the real model. In SurfaceScoreEngine.cs (near the report records):

```csharp
public sealed record ConservationAnchorMethod(string Name, string Returns);
public sealed record ConservationAnchor(
    string Key,            // fully-qualified identity: "<section>::<ns>.<TypeName>"
    string Section,
    string Role,           // primaryInfoDto|settingsInfoDto|cacheDto|readServiceInterface|fullServiceInterface|readShard
    IReadOnlyList<string> Paths,                       // DTO anchors (recursive)
    IReadOnlyList<ConservationAnchorMethod> Methods,   // interface/shard anchors
    Dictionary<string, int> ByRule);                   // per-anchor points
```
Add `public List<ConservationAnchor> ConservationAnchors { get; set; } = new();` to `ScoreReport`.

`BuildConservationAnchors(SectionArchitecture arch, ScoreReport report)`:
  - For each section: emit DTO anchors (primary/settings/cache) keyed `"{section}::{display}"`, role per kind, `Paths` from the analyzer's `DtoAnchor.Paths`, empty Methods, ByRule from the section's DTO-attributed points if tracked (DTO points are not per-anchor in the engine; set ByRule empty for DTO anchors or populate `publicDtoType`/property rule sums by matching `ScoreEntry.Symbol == typeName && File == ...` - best-effort: sum entries whose `Symbol` equals the DTO type name within the section group).
  - For each read/full interface in the section: emit an InterfaceAnchor with `Methods = {name, returns}`; ByRule = sum of that interface's entries by rule (match `ScoreEntry` where `Symbol == methodName` for methods on that interface, grouped by rule; readServiceInterfaceMethod + readSurfaceProjectionMethod for read; fullServiceInterfaceMethod for full). Best-effort attribution by (group, ruleset).
  - For each shard: emit a shard anchor.
  - Always emitted regardless of `--top-symbols` (this is report-level, independent of the command's top cap).

  In `SurfaceScoreCommand.WriteJson` payload, add a top-level key (additive):
```csharp
            conservationAnchors = report.ConservationAnchors.Select(a => new {
                key = a.Key, section = a.Section, role = a.Role,
                paths = a.Paths, methods = a.Methods.Select(m => new { name = m.Name, returns = m.Returns }),
                byRule = a.ByRule
            }).ToArray(),
```

- [ ] **Step 4: Run -> PASS**; add a JSON shape test (run the built DLL or call WriteJson via a small harness - simplest: assert on `report.ConservationAnchors` in xUnit; a CLI/jq check happens in dogfooding). Full suite green.
- [ ] **Step 5: Commit** `feat(surface-score): emit conservationAnchors (FQ-keyed, recursive DTO paths, interface methods)`

---

## Task 7: Cache-DTO inference from a caching decorator

When `cacheDto` is not configured, infer it: find a class implementing the section's read interface whose name matches `Cached*`/`*CachingDecorator`/`*Cache`, and read the value type of its cache field (`Dictionary<TKey,TValue>`, or an `IMemoryCache`/`ConcurrentDictionary` populated with a DTO). If found, `CacheDto` = that DTO, provenance `inferred:<decorator>`. If unresolved, cacheDto stays at default-primary (or none).

**Files:**
- Modify: `src/Reforge/SectionShapeAnalyzer.cs`
- Modify: `CampFixtures.cs` (a caching decorator whose cache value is a *different* DTO so the test proves inference, e.g. `CampCacheEntry`)
- Test: `SectionShapeAnalyzerTests`

- [ ] **Step 1: Fixture**:

```csharp
public sealed class CampCacheEntry { public Guid Id { get; set; } public string Name { get; set; } = ""; }
public sealed class CachedCampReadService : ICampServiceRead
{
    private readonly Dictionary<Guid, CampCacheEntry> _cache = new();
    private readonly ICampServiceRead _inner;
    public CachedCampReadService(ICampServiceRead inner) => _inner = inner;
    public Task<CampInfo> GetByIdAsync(Guid id, CancellationToken ct = default) => _inner.GetByIdAsync(id, ct);
    public Task<CampSettingsInfo> GetSettingsAsync(Guid campId, CancellationToken ct = default) => _inner.GetSettingsAsync(campId, ct);
    public Task<bool> IsUserCampLeadAsync(Guid campId, Guid userId, CancellationToken ct = default) => _inner.IsUserCampLeadAsync(campId, userId, ct);
    public Task<List<CampSummary>> GetCampSummariesForYearAsync(int year, CancellationToken ct = default) => _inner.GetCampSummariesForYearAsync(year, ct);
}
```

- [ ] **Step 2: Failing test**:

```csharp
[Fact]
public async Task Analyze_InfersCacheDtoFromCachingDecorator()
{
    var cfg = new SurfaceScoreConfig { Sections = { ["Camp"] = new SectionRule {
        RepositoryInterfaces = { "ICampRepository" }, ServiceInterfaces = { "ICampSectionService" },
        ReadServiceInterfaces = { "ICampServiceRead" } } } };   // cacheDto NOT configured
    cfg.BuildEffectiveSections();
    var dir = LocationHelper.GetSolutionDirectory(_fixture.Solution);
    var classified = (await SolutionClassifier.ClassifyAsync(_fixture.Solution, cfg, dir, CancellationToken.None)).ToList();
    var arch = await SectionShapeAnalyzer.AnalyzeAsync(_fixture.Solution, classified, cfg, dir, CancellationToken.None);
    var camp = arch.Sections.Single(s => s.Name == "Camp");
    Assert.Equal("CampCacheEntry", camp.CacheDto!.Display.Split('.').Last());
    Assert.StartsWith("inferred:", camp.CacheDtoProvenance);
}
```

- [ ] **Step 3: Implement** the inference (decorator name match + cache-field value-type extraction). Guard: only when `rule.CacheDto` is null.
- [ ] **Step 4: Run -> PASS**; full suite green.
- [ ] **Step 5: Commit** `feat(surface-score): infer cacheDto from caching decorator`

---

## Task 8: `reforge section-shape` command + advisory candidates

**Files:**
- Modify: `src/Reforge/SectionShapeAnalyzer.cs` (advisories: derivableReadMethods/missingInfoFacts/cacheFactCandidates)
- Create: `src/Reforge/Commands/SectionShapeCommand.cs`
- Modify: `src/Reforge/Program.cs` (register)
- Create: `test/Reforge.Tests/SectionShapeTests.cs`

- [ ] **Step 1: Advisories in the analyzer.** `derivableReadMethods` = the charged read methods, each with a best-effort `TargetDto` (primaryInfoDto for projection/predicate/UI, settingsInfoDto for scalar settings reads) + a `Hint` naming likely covering fields. `missingInfoFacts` = facts implied by charged methods not present on the primary/settings inventory (best-effort: derive a fact name from the method, e.g. `IsUserCampLeadAsync` -> `CampInfo.<season>.Members[].IsLead`; if not in the inventory paths, add `{fact, targetDto}`). `cacheFactCandidates` = charged read methods answerable from the cache DTO.

- [ ] **Step 2: Command + failing test.** `SectionShapeCommand.Create(solutionOption, formatOption, limitOption)` mirroring `SurfaceScoreCommand`: `--config`, `--section`, `--format compact|markdown|json`. Opens workspace, classifies, analyzes, renders. JSON shape includes per-section: ownedRepositories, read/full interfaces, primaryInfoDto/settingsInfoDto/cacheDto (+provenance), readShards, readSurfaceCallers, writeSurfaceCallers, crossSectionRepository/entity (reuse), missing, grandfathered, escapeHatches, and `advisory: { derivableReadMethods, missingInfoFacts, cacheFactCandidates, crossSectionWriteSurfaceUnverified }`.

```csharp
namespace Reforge.Tests;

[Collection("SampleSolution")]
public class SectionShapeTests
{
    private readonly SampleSolutionFixture _fixture;
    public SectionShapeTests(SampleSolutionFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task SectionShape_Json_RendersCampShapeAndAdvisory()
    {
        // Drive the analyzer directly (command IO is covered by a CLI smoke test in dogfooding).
        var cfg = new SurfaceScoreConfig { Sections = { ["Camp"] = new SectionRule {
            RepositoryInterfaces = { "ICampRepository" }, ServiceInterfaces = { "ICampSectionService" },
            ReadServiceInterfaces = { "ICampServiceRead" },
            EscapeHatchReadMethods = { new EscapeHatchReadMethod { Method = "ICampServiceRead.IsUserCampLeadAsync", Reason = "legacy", Since = "2026-02", Owner = "camps" } } } } };
        cfg.BuildEffectiveSections();
        var dir = LocationHelper.GetSolutionDirectory(_fixture.Solution);
        var classified = (await SolutionClassifier.ClassifyAsync(_fixture.Solution, cfg, dir, CancellationToken.None)).ToList();
        var arch = await SectionShapeAnalyzer.AnalyzeAsync(_fixture.Solution, classified, cfg, dir, CancellationToken.None);
        var camp = arch.Sections.Single(s => s.Name == "Camp");

        Assert.NotNull(camp.PrimaryInfoDto);
        Assert.NotEmpty(camp.DerivableReadMethods);                       // advisory present
        Assert.Single(camp.EscapeHatches);                               // visible debt rendered
        Assert.Contains(camp.ChargedReadMethods, m => m.Method == "IsUserCampLeadAsync" && m.EscapeHatch);
    }
}
```

- [ ] **Step 3: Implement** the command + register in Program.cs (`rootCommand.Add(SectionShapeCommand.Create(solutionOption, formatOption, limitOption));` after the surface-score line).
- [ ] **Step 4: Run -> PASS**; full suite green; `dotnet build` clean (command compiles).
- [ ] **Step 5: Commit** `feat: section-shape command + advisory candidates`

---

## Task 9: Wire-up sweep + Section 6 coverage checks

- [ ] Confirm the search-shape gate (Plan A `ReadSurfaceTests.Classify_SearchNamedButWrongShape_IsProjectionNotSearch`) still passes - charged read surcharge relies on it.
- [ ] Add a test asserting `conservationAnchors` are emitted even with `--top-symbols 0` semantics (report-level list is independent of the command's top cap; assert `report.ConservationAnchors` non-empty regardless).
- [ ] Run the whole suite; fix any count assertions disturbed by the new fixtures (the new public interfaces/DTOs add surface points to the default-config "Services"/"Core" groups). Update disturbed hard-coded counts and note them in the commit.
- [ ] **Commit** `test(surface-score): section-architecture coverage + fixture count updates`

---

## Task 10: Glossary/CHANGELOG/dogfood + ship v0.19.0

- [ ] CHANGELOG.md: prepend a `## v0.19.0 - section architecture` entry (what + why; ASCII only).
- [ ] Dogfood (memory [[reference_dogfooding]]): run the BUILT DLL (not `dotnet run` - it doubles stderr):
  - `dotnet build Reforge.slnx`
  - `dotnet src/Reforge/bin/Debug/net10.0/Reforge.dll surface-score --solution test/SampleSolution/SampleSolution.slnx --format json` -> jq `.conservationAnchors`, `.byRule.readSurfaceProjectionMethod` (note: sample has NO config, so configured-section rules will be quiet; create a temp `reforge.surface-score.json` Camp config beside the sample slnx for the dogfood run, then remove it - single-file `rm -f` is allowed).
  - `dotnet src/Reforge/bin/Debug/net10.0/Reforge.dll section-shape --solution test/SampleSolution/SampleSolution.slnx --config <tempCampConfig> --format json` -> inspect a section.
  - Humans (`H:\source\humans\Humans.slnx`, `timeout 420`): `surface-score --format json` -> conservationAnchors + the 5 new rules; `section-shape`. Note build.degraded may be true (issue #9). Report concrete numbers.
  - Camps branch: `gh pr list` on Humans repo; if an open Camps PR exists, validate against a TEMP read-only worktree at that branch (cleaned up; never touch the main Humans checkout). Else fall back to Humans-main + sample fixtures, documenting the gap.
- [ ] Ship (locked decision, [[project_b_c_run]]): commit features; SEPARATE csproj version-bump commit to 0.19.0; `git push origin main`; `dotnet pack src/Reforge/Reforge.csproj -c Release -o src/Reforge/nupkg`; `dotnet tool update --global --add-source src/Reforge/nupkg Reforge --version 0.19.0`; verify `reforge --version`.

---

## Self-review notes

- **Spec coverage:** Item1 section-shape (T8) + SectionShapeAnalyzer (T2); Item2 crossSectionWriteSurface (T5); Item3 canonical-DTO-not-bag-gaming = Plan C gate, anchors (T6) make it auditable; Items4/8 advisory (T8) + scored surrogate readSurfaceProjectionMethod (T3); Item5 readSurfaceProjectionMethod (T3); Item9 missing* (T4); cache inference (T7); glossary/weights/axis (T1); conservationAnchors (T6); fixtures/tests throughout. Item6 helper-extraction + the conservation gate decision tree = Plan C.
- **Type consistency:** `SectionShapeAnalyzer.AnalyzeAsync(Solution, List<ClassifiedType>, SurfaceScoreConfig, string, CancellationToken) -> Task<SectionArchitecture>` fixed in T2, consumed unchanged after. `ConservationAnchor` introduced in T3 (placeholder) and finalized in T6 - keep the final shape from T3 to avoid churn. `ChargedReadMethod` carries an in-memory `IMethodSymbol`/file/line for `AddEntry`.
- **Behavioral, not nominal:** read-method charging via `ReadSurface.Classify` (shape), cross-section read-cover via observed invocations + escape analysis - no name globs decide scoring.
- **No new gameable reward:** all five are penalties or surcharges (positive points = worse); advisories are 0 pts; derivability stays advisory; the conservation reward lives in Plan C's baseline gate.
- **Risk:** new public fixtures shift default-config group counts; existing tests are mostly behavioral, but fix any disturbed hard counts in T9. Escape analysis (T5) is the riskiest; its tests pin both the confident and unverified paths.
