# Section-Architecture Scoring - Plan C (baseline conservation gate) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task, TDD, commit per task. Steps use checkbox (`- [ ]`) syntax. Implementation code blocks are strong drafts; red-green is the source of truth - refine in execution, keep tests as the contract.

**Goal:** Under `--baseline`, classify what happened to removed read/service behavior per section: a real consolidation into the canonical DTOs (`canonical-consolidation`, improvement), a sideways move into a helper (`helperExtractionNoConceptDeleted`, not an improvement - checked FIRST), or a lost capability (`capability-evaporation`). Decisions are audited by per-method evidence rows and are immune to unrelated churn because coverage is checked against the `conservationAnchors` inventories, never inferred from score deltas.

**Architecture:** Plan B emits `conservationAnchors` (DTO path inventories + interface method lists, FQ-keyed per section). Plan C adds (1) a `helperCandidates` list to the report + JSON so the gate can detect a NEW stateless sink absorbing removed methods, and (2) the gate itself in `SurfaceScoreBaseline.Compare`: it diffs baseline vs current interface-method anchors to find removed methods, runs helper-absorption-first, then a best-effort coverage check against the primary/cache/settings DTO path inventories + documented shards, and emits the three `SuspiciousImprovement` kinds plus per-method evidence.

**Tech Stack:** .NET 10, Roslyn, System.Text.Json, xUnit. Build: `dotnet build Reforge.slnx`. Test: `dotnet test Reforge.slnx`. Windows + Git Bash; never chain `cd x && cmd` (hook-blocked). Docs ASCII only.

**Spec:** `docs/superpowers/specs/2026-05-29-section-architecture-scoring-design.md` Section 4 + Item 6 (the gate; the anchors it reads landed in Plan B).

**Verified ground state (2026-05-30):** main @ v0.19.0, 112/112 tests green. Plan B in place: `SurfaceScoreEngine.ScoreAsync` emits `report.ConservationAnchors` (record `ConservationAnchor(string Key, string Section, string Role, IReadOnlyList<string> Paths, IReadOnlyList<ConservationAnchorMethod> Methods, Dictionary<string,int> ByRule)`; roles `primaryInfoDto|settingsInfoDto|cacheDto|readServiceInterface|fullServiceInterface|readShard`). `SurfaceScoreCommand.WriteJson` serializes `conservationAnchors`. The existing baseline gate (`SurfaceScoreBaseline.Compare(ScoreReport now, string baselineJsonPath) -> BaselineComparison`) reads `surfaceTotal`/`internalComplexityTotal`/`byRule`/`groups` from the baseline JSON, produces `ScopeDelta` per scope + `SuspiciousImprovement` entries (Pareto gate + parameter-bag / dispatcher / god-method composition flags). The command adds `baseline.Suspicious` into `report.SuspiciousImprovements` and serializes them under `suspiciousImprovements`. `MissingInfoFact(string Fact, string TargetDto)` already exists (SectionShapeAnalyzer.cs).

---

## Key integration points (verified)

- `SurfaceScoreEngine.ScoreAsync` (SurfaceScoreEngine.cs): after anchors are built, also build `report.HelperCandidates` (Task 1).
- `ScoreReport` (SurfaceScoreEngine.cs): add `List<HelperCandidate> HelperCandidates`.
- `SurfaceScoreCommand.WriteJson` payload (~L590): add `helperCandidates` next to `conservationAnchors`; add `conservationEvidence` (Task 4).
- `SurfaceScoreBaseline.Compare` (SurfaceScoreBaseline.cs:54): after the per-scope loop, run the conservation gate (Task 3). Parse baseline `conservationAnchors` + `helperCandidates` from the JSON `root`.
- `BaselineComparison` (SurfaceScoreBaseline.cs:19): add `List<ConservationVerdict> ConservationVerdicts`.

---

## Data model (fix once; later tasks depend on it)

In `SurfaceScoreEngine.cs` (near the report records):

```csharp
/// <summary>A stateless sink (static class, extension holder, or fieldless non-interface-backed
/// class) and its public method names — a candidate destination for helper-extraction gaming.</summary>
public sealed record HelperCandidate(string Display, IReadOnlyList<string> Methods);
```

In `SurfaceScoreBaseline.cs`:

```csharp
/// <summary>Per-removed-method audit row for a conservation verdict.</summary>
public sealed record MethodEvidence(
    string RemovedMethod,
    string CoverageKind,         // existingDtoFact|addedDtoFact|documentedShard|helper|uncovered|ambiguous
    string? TargetDto,           // primaryInfoDto|cacheDto|settingsInfoDto|readShard|null
    IReadOnlyList<string> CoveredBy,
    IReadOnlyList<MissingInfoFact> MissingInfoFacts);

/// <summary>Section-scoped conservation verdict: the roll-up kind + the evidence rows behind it.</summary>
public sealed record ConservationVerdict(
    string Section,
    string Kind,                 // canonical-consolidation|helperExtractionNoConceptDeleted|capability-evaporation
    bool Improvement,
    string Message,
    IReadOnlyList<MethodEvidence> Methods);
```

---

## Task 1: `helperCandidates` in the report + JSON

**Files:**
- Modify: `src/Reforge/SurfaceScoreEngine.cs` (`HelperCandidate` record, `ScoreReport.HelperCandidates`, a `BuildHelperCandidates` pass called from `ScoreAsync`)
- Modify: `src/Reforge/Commands/SurfaceScoreCommand.cs` (`WriteJson` payload)
- Test: `test/Reforge.Tests/SurfaceScoreTests.cs`

- [ ] **Step 1: Failing test** - add to `SurfaceScoreTests.cs`:

```csharp
[Fact]
public async Task HelperCandidates_IncludeStatelessSinks()
{
    var report = await ScoreDefaultAsync();
    // A static helper class with public methods is a helper candidate; a service with
    // instance fields (UserService) is not.
    Assert.Contains(report.HelperCandidates, h => h.Display.EndsWith("CampReadModelProjection"));
    Assert.DoesNotContain(report.HelperCandidates, h => h.Display.EndsWith("UserService"));
}
```

(Use the existing `ScoreDefaultAsync()` helper in `SurfaceScoreTests.cs`. The `CampReadModelProjection` fixture is added in Task 5; if running this task first, add the minimal fixture from Task 5 Step 1 now.)

- [ ] **Step 2: Run -> FAIL.**
- [ ] **Step 3: Implement.** Add the `HelperCandidate` record (Data model). Add to `ScoreReport`:

```csharp
public List<HelperCandidate> HelperCandidates { get; set; } = new();
```

In `ScoreAsync`, after `report.ConservationAnchors = BuildConservationAnchors(...)`, add:

```csharp
        report.HelperCandidates = BuildHelperCandidates(classified);
```

New method (a helper is a static class, or a class with no instance fields that is not backed by a source interface — the broad "stateless sink" the spec wants):

```csharp
    private static List<HelperCandidate> BuildHelperCandidates(List<ClassifiedType> classified)
    {
        var helpers = new List<HelperCandidate>();
        foreach (var c in classified)
        {
            if (c.Type.TypeKind != TypeKind.Class) continue;
            bool hasInstanceField = c.Type.GetMembers().OfType<IFieldSymbol>().Any(f => !f.IsStatic && !f.IsImplicitlyDeclared);
            bool interfaceBacked = c.Type.AllInterfaces.Any(i => i.Locations.Any(l => l.IsInSource));
            bool stateless = c.Type.IsStatic || (!hasInstanceField && !interfaceBacked);
            if (!stateless) continue;

            var methods = c.Type.GetMembers().OfType<IMethodSymbol>()
                .Where(m => m.MethodKind == MethodKind.Ordinary && m.AssociatedSymbol is null
                            && !m.IsImplicitlyDeclared && m.DeclaredAccessibility == Accessibility.Public)
                .Select(m => m.Name).ToList();
            if (methods.Count == 0) continue;
            helpers.Add(new HelperCandidate(c.Type.ToDisplayString(), methods));
        }
        return helpers;
    }
```

In `SurfaceScoreCommand.WriteJson` payload, after the `conservationAnchors` key:

```csharp
            helperCandidates = report.HelperCandidates.Select(h => new { display = h.Display, methods = h.Methods }).ToArray(),
```

- [ ] **Step 4: Run -> PASS**; full suite green.
- [ ] **Step 5: Commit** `feat(surface-score): emit helperCandidates (stateless sinks) for the conservation gate`

---

## Task 2: Parse baseline `conservationAnchors` + `helperCandidates`

Teach the baseline reader to load the Plan B anchor inventories and Task 1 helpers from the baseline JSON, and to surface a precision diagnostic when they are absent (pre-v0.19 baseline).

**Files:**
- Modify: `src/Reforge/SurfaceScoreBaseline.cs` (parsing + new fields on `BaselineComparison`)
- Test: `test/Reforge.Tests/SurfaceScoreBaselineTests.cs` (create if absent)

- [ ] **Step 1: Failing test** - create/extend `SurfaceScoreBaselineTests.cs`:

```csharp
namespace Reforge.Tests;

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
            var now = new ScoreReport();
            var cmp = SurfaceScoreBaseline.Compare(now, path);
            Assert.True(cmp.BaselineAnchorsMissing);
        }
        finally { File.Delete(path); }
    }
}
```

- [ ] **Step 2: Run -> FAIL.**
- [ ] **Step 3: Implement.** In `BaselineComparison` add:

```csharp
    public bool BaselineAnchorsMissing { get; set; }
    public List<ConservationVerdict> ConservationVerdicts { get; } = new();
```

Add the `MethodEvidence` + `ConservationVerdict` records (Data model). In `Compare`, after building `comparison`, parse anchors + helpers from `root`:

```csharp
        var baseAnchors = ReadAnchors(root);            // Dictionary<section, BaselineAnchors>
        var baseHelpers = ReadHelperDisplays(root);     // HashSet<string>
        comparison.BaselineAnchorsMissing = !root.TryGetProperty("conservationAnchors", out _);
```

Add a baseline-anchors model + readers:

```csharp
    private sealed record AnchorInventory(string Role, List<string> Paths, List<string> Methods);
    // Per section: role -> inventory. Roles: primaryInfoDto/settingsInfoDto/cacheDto/readShard (paths)
    // and readServiceInterface/fullServiceInterface (methods).
    private sealed class SectionAnchors
    {
        public List<string> PrimaryPaths = new();
        public List<string> SettingsPaths = new();
        public List<string> CachePaths = new();
        public List<string> ShardMethods = new();
        public List<(string Name, string Returns)> InterfaceMethods = new();
    }

    private static Dictionary<string, SectionAnchors> ReadAnchors(JsonElement root)
    {
        var bySection = new Dictionary<string, SectionAnchors>(StringComparer.OrdinalIgnoreCase);
        if (!root.TryGetProperty("conservationAnchors", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return bySection;
        foreach (var a in arr.EnumerateArray())
        {
            var section = a.TryGetProperty("section", out var s) ? s.GetString() ?? "" : "";
            var role = a.TryGetProperty("role", out var r) ? r.GetString() ?? "" : "";
            if (!bySection.TryGetValue(section, out var sa)) { sa = new SectionAnchors(); bySection[section] = sa; }
            var paths = a.TryGetProperty("paths", out var p) && p.ValueKind == JsonValueKind.Array
                ? p.EnumerateArray().Select(x => x.GetString() ?? "").ToList() : new List<string>();
            switch (role)
            {
                case "primaryInfoDto": sa.PrimaryPaths.AddRange(paths); break;
                case "settingsInfoDto": sa.SettingsPaths.AddRange(paths); break;
                case "cacheDto": sa.CachePaths.AddRange(paths); break;
                case "readServiceInterface":
                case "fullServiceInterface":
                    if (a.TryGetProperty("methods", out var ms) && ms.ValueKind == JsonValueKind.Array)
                        foreach (var m in ms.EnumerateArray())
                            sa.InterfaceMethods.Add((m.TryGetProperty("name", out var mn) ? mn.GetString() ?? "" : "",
                                                     m.TryGetProperty("returns", out var mr) ? mr.GetString() ?? "" : ""));
                    break;
                case "readShard":
                    if (a.TryGetProperty("methods", out var sm) && sm.ValueKind == JsonValueKind.Array)
                        foreach (var m in sm.EnumerateArray())
                            sa.ShardMethods.Add(m.TryGetProperty("name", out var smn) ? smn.GetString() ?? "" : "");
                    break;
            }
        }
        return bySection;
    }

    private static HashSet<string> ReadHelperDisplays(JsonElement root)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (root.TryGetProperty("helperCandidates", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var h in arr.EnumerateArray())
                if (h.TryGetProperty("display", out var d) && d.GetString() is { } disp) set.Add(disp);
        return set;
    }
```

- [ ] **Step 4: Run -> PASS**; full suite green.
- [ ] **Step 5: Commit** `feat(baseline): parse conservationAnchors + helperCandidates from baseline JSON`

---

## Task 3: The conservation gate (decision tree + coverage check)

**Files:**
- Modify: `src/Reforge/SurfaceScoreBaseline.cs` (`RunConservationGate` + helpers, called from `Compare`)
- Test: `test/Reforge.Tests/SurfaceScoreBaselineTests.cs`

- [ ] **Step 1: Failing tests** - add to `SurfaceScoreBaselineTests.cs` a helper that builds a current `ScoreReport` with one section's anchors + a synthetic baseline JSON, then asserts each kind. (Full code in Step 3; build incrementally.) Cover: existingDtoFact -> canonical-consolidation; uncovered core read -> capability-evaporation; new helper absorbs removed method (even with ambiguous coverage) -> helperExtractionNoConceptDeleted; settings fact -> targetDto settingsInfoDto.

```csharp
    private static (ScoreReport now, string baselinePath) Setup(
        string section, List<ConservationAnchor> nowAnchors, List<HelperCandidate> nowHelpers,
        List<(string name,string returns)> baselineMethods, List<string> baselinePrimaryPaths,
        int baselineReadSurface, int nowReadSurface, List<string> baselineHelpers)
    {
        var now = new ScoreReport();
        now.ConservationAnchors.AddRange(nowAnchors);
        now.HelperCandidates.AddRange(nowHelpers);
        var g = new GroupScore { Name = section };
        // read-surface points drop: baseline had more readServiceInterfaceMethod than now.
        g.ByRule["readServiceInterfaceMethod"] = nowReadSurface;
        now.Groups[section] = g;

        var baseline = new
        {
            total = baselineReadSurface, surfaceTotal = baselineReadSurface, internalComplexityTotal = 0,
            byRule = new Dictionary<string,int>(),
            groups = new[] { new { name = section, total = baselineReadSurface, surfaceTotal = baselineReadSurface,
                internalComplexityTotal = 0, byRule = new Dictionary<string,int>{ ["readServiceInterfaceMethod"] = baselineReadSurface } } },
            conservationAnchors = new object[]
            {
                new { key = $"{section}::Foo.{section}Info", section, role = "primaryInfoDto", paths = baselinePrimaryPaths, methods = Array.Empty<object>() },
                new { key = $"{section}::Foo.I{section}ServiceRead", section, role = "readServiceInterface", paths = Array.Empty<string>(),
                      methods = baselineMethods.Select(m => new { name = m.name, returns = m.returns }).ToArray() },
            },
            helperCandidates = baselineHelpers.Select(h => new { display = h, methods = Array.Empty<string>() }).ToArray()
        };
        var path = Path.GetTempFileName();
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(baseline));
        return (now, path);
    }

    private static ConservationAnchor PrimaryAnchor(string section, params string[] paths)
        => new($"{section}::Foo.{section}Info", section, "primaryInfoDto", paths, Array.Empty<ConservationAnchorMethod>(), new());
    private static ConservationAnchor ReadIface(string section, params (string n,string r)[] methods)
        => new($"{section}::Foo.I{section}ServiceRead", section, "readServiceInterface", Array.Empty<string>(),
               methods.Select(m => new ConservationAnchorMethod(m.n, m.r)).ToList(), new());

    [Fact]
    public void Gate_ExistingDtoFact_IsCanonicalConsolidation()
    {
        // Baseline read iface had GetMembersAsync; now it's gone; the fact "Members" is on CampInfo
        // in BOTH baseline and current inventories -> existingDtoFact -> canonical-consolidation.
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
        // Removed method absorbed by a NEW helper, with NO DTO coverage (ambiguous-leaning) ->
        // helperExtractionNoConceptDeleted, NOT canonical-consolidation. The core gaming-hole guard.
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
        // Removed method returns the primary DTO and the fact isn't anywhere -> capability-evaporation.
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
            new("Camp::Foo.CampSettingsInfo", "Camp", "settingsInfoDto", new[]{ "CampSettingsInfo.NameLockDate" }, Array.Empty<ConservationAnchorMethod>(), new()),
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
```

Note: `Setup` puts the settings paths only when `nowAnchors` includes a settings anchor; the parser reads current anchors from `now.ConservationAnchors`, baseline from the JSON. Keep baseline settings empty so the fact is `addedDtoFact` on settings (still covered -> canonical-consolidation).

- [ ] **Step 2: Run -> FAIL.**
- [ ] **Step 3: Implement.** In `Compare`, after parsing and the per-scope loop:

```csharp
        RunConservationGate(now, baseAnchors, baseHelpers, comparison);
```

Implement the gate. Current section anchors come from `now.ConservationAnchors` (group by section); current helpers from `now.HelperCandidates`.

```csharp
    private static void RunConservationGate(ScoreReport now,
        Dictionary<string, SectionAnchors> baseAnchors, HashSet<string> baseHelpers, BaselineComparison cmp)
    {
        // Index current anchors per section.
        var nowBySection = now.ConservationAnchors.GroupBy(a => a.Section, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        // NEW helper method names (helpers present now but not in the baseline), stripped of Async.
        var newHelperMethods = new HashSet<string>(StringComparer.Ordinal);
        foreach (var h in now.HelperCandidates)
            if (!baseHelpers.Contains(h.Display))
                foreach (var m in h.Methods) newHelperMethods.Add(StripAsync(m));

        foreach (var (section, baseSa) in baseAnchors)
        {
            // Removed read/service methods = baseline interface methods not in current.
            var nowAnchorsForSection = nowBySection.TryGetValue(section, out var na) ? na : new List<ConservationAnchor>();
            var nowMethodNames = nowAnchorsForSection
                .Where(a => a.Role is "readServiceInterface" or "fullServiceInterface")
                .SelectMany(a => a.Methods.Select(m => m.Name))
                .ToHashSet(StringComparer.Ordinal);
            var removed = baseSa.InterfaceMethods.Where(m => !nowMethodNames.Contains(m.Name)).ToList();
            if (removed.Count == 0) continue; // read surface did not drop via method removal

            // Current inventories.
            var nowPrimary = Paths(nowAnchorsForSection, "primaryInfoDto");
            var nowSettings = Paths(nowAnchorsForSection, "settingsInfoDto");
            var nowCache = Paths(nowAnchorsForSection, "cacheDto");
            var nowShards = nowAnchorsForSection.Where(a => a.Role == "readShard").SelectMany(a => a.Methods.Select(m => m.Name)).ToList();

            // Helper absorption first.
            var helperAbsorbed = removed.Where(m => newHelperMethods.Contains(StripAsync(m.Name))).ToList();
            var evidence = new List<MethodEvidence>();

            if (helperAbsorbed.Count > 0)
            {
                foreach (var m in removed)
                    evidence.Add(helperAbsorbed.Contains(m)
                        ? new MethodEvidence(m.Name, "helper", null, Array.Empty<string>(), Array.Empty<MissingInfoFact>())
                        : Cover(m, baseSa, nowPrimary, nowSettings, nowCache, nowShards));
                var moved = string.Join(", ", helperAbsorbed.Select(m => m.Name));
                cmp.ConservationVerdicts.Add(new ConservationVerdict(section, "helperExtractionNoConceptDeleted", false,
                    $"{section}: {helperAbsorbed.Count} read/service method(s) moved into a new helper instead of being deleted ({moved}).", evidence));
                cmp.Suspicious.Add(new SuspiciousImprovement(section, "helperExtractionNoConceptDeleted",
                    $"{section}: read/service surface dropped but {helperAbsorbed.Count} method(s) moved sideways into a new stateless helper ({moved}) — concept not deleted.",
                    0, 0, false));
                continue;
            }

            foreach (var m in removed)
                evidence.Add(Cover(m, baseSa, nowPrimary, nowSettings, nowCache, nowShards));

            bool anyUncovered = evidence.Any(e => e.CoverageKind == "uncovered");
            bool allCovered = evidence.All(e => e.CoverageKind is "existingDtoFact" or "addedDtoFact" or "documentedShard");

            if (allCovered)
            {
                cmp.ConservationVerdicts.Add(new ConservationVerdict(section, "canonical-consolidation", true,
                    $"{section}: read surface consolidated into the canonical DTOs (-{removed.Count} method(s)); all removed facts covered.", evidence));
                cmp.Suspicious.Add(new SuspiciousImprovement(section, "canonical-consolidation",
                    $"{section}: read surface consolidated into the canonical DTOs (-{removed.Count} method(s)); facts covered.", 0, 0, true));
            }
            else if (anyUncovered)
            {
                var lost = string.Join(", ", evidence.Where(e => e.CoverageKind == "uncovered").Select(e => e.RemovedMethod));
                cmp.ConservationVerdicts.Add(new ConservationVerdict(section, "capability-evaporation", false,
                    $"{section}: read surface dropped but removed facts are uncovered ({lost}) — capability evaporated or leaked to callers.", evidence));
                cmp.Suspicious.Add(new SuspiciousImprovement(section, "capability-evaporation",
                    $"{section}: read surface dropped but {lost} not covered by any consolidation target.", 0, 0, false));
            }
            else
            {
                // Remaining uncertainty -> ambiguity bias toward consolidation, but the ambiguity is visible.
                cmp.ConservationVerdicts.Add(new ConservationVerdict(section, "canonical-consolidation", true,
                    $"{section}: read surface consolidated (-{removed.Count} method(s)); some coverage ambiguous (see missingInfoFacts).", evidence));
                cmp.Suspicious.Add(new SuspiciousImprovement(section, "canonical-consolidation",
                    $"{section}: read surface consolidated (-{removed.Count} method(s)); some facts ambiguous, surfaced as advisory.", 0, 0, true));
            }
        }
    }

    private static List<string> Paths(List<ConservationAnchor> anchors, string role)
        => anchors.Where(a => a.Role == role).SelectMany(a => a.Paths).ToList();

    private static string StripAsync(string n)
        => n.EndsWith("Async", StringComparison.Ordinal) && n.Length > 5 ? n[..^5] : n;

    /// <summary>Best-effort coverage of one removed method against the current inventories.</summary>
    private static MethodEvidence Cover((string Name, string Returns) m, SectionAnchors baseSa,
        List<string> nowPrimary, List<string> nowSettings, List<string> nowCache, List<string> nowShards)
    {
        var token = FactToken(m.Name);
        bool settingsy = LooksSettings(m.Name) || IsScalar(m.Returns);

        // Settings facts consolidate into settingsInfoDto; everything else primary/cache.
        if (settingsy && nowSettings.Any(p => Contains(p, token)))
            return new MethodEvidence(m.Name, baseSa.SettingsPaths.Any(p => Contains(p, token)) ? "existingDtoFact" : "addedDtoFact",
                "settingsInfoDto", nowSettings.Where(p => Contains(p, token)).ToList(), Array.Empty<MissingInfoFact>());

        if (nowPrimary.Any(p => Contains(p, token)))
            return new MethodEvidence(m.Name, baseSa.PrimaryPaths.Any(p => Contains(p, token)) ? "existingDtoFact" : "addedDtoFact",
                "primaryInfoDto", nowPrimary.Where(p => Contains(p, token)).ToList(), Array.Empty<MissingInfoFact>());

        if (nowCache.Any(p => Contains(p, token)))
            return new MethodEvidence(m.Name, "addedDtoFact", "cacheDto", nowCache.Where(p => Contains(p, token)).ToList(), Array.Empty<MissingInfoFact>());

        if (nowShards.Any(sm => Contains(sm, token) || StripAsync(sm) == StripAsync(m.Name)))
            return new MethodEvidence(m.Name, "documentedShard", "readShard", nowShards.Where(sm => Contains(sm, token)).ToList(), Array.Empty<MissingInfoFact>());

        // Not covered. A removed PRIMITIVE read (returns the primary Info DTO) is a real capability
        // loss -> uncovered. A charged-shape read (bool/scalar/non-primary DTO) is derivable-but-
        // unproven -> ambiguous (lean consolidation, surface the fact as advisory).
        var target = settingsy ? "settingsInfoDto" : "primaryInfoDto";
        if (ReturnsInfoDto(m.Returns))
            return new MethodEvidence(m.Name, "uncovered", target, Array.Empty<string>(),
                new[] { new MissingInfoFact($"{target}.{token}", target) });
        return new MethodEvidence(m.Name, "ambiguous", target, Array.Empty<string>(),
            new[] { new MissingInfoFact($"{target}.{token}", target) });
    }

    private static bool Contains(string path, string token)
        => token.Length > 0 && path.Contains(token, StringComparison.OrdinalIgnoreCase);
    private static bool LooksSettings(string name)
        => name.Contains("Settings", StringComparison.OrdinalIgnoreCase)
        || name.Contains("LockDate", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Year", StringComparison.OrdinalIgnoreCase);
    private static bool IsScalar(string returns)
    {
        var r = returns.Replace("Task<", "").Replace("ValueTask<", "").TrimEnd('>').Trim();
        return r is "bool" or "int" or "long" or "string" or "Guid" or "DateTime" or "DateTimeOffset" or "decimal" or "double";
    }
    private static bool ReturnsInfoDto(string returns)
        => returns.Contains("Info", StringComparison.Ordinal) && !IsScalar(returns);

    private static string FactToken(string method)
    {
        var n = StripAsync(method);
        foreach (var pre in new[] { "Get", "Find", "Load", "Build", "Is", "Has" })
            if (n.StartsWith(pre, StringComparison.Ordinal) && n.Length > pre.Length) { n = n[pre.Length..]; break; }
        // Take the leading noun-ish token: strip a trailing "ForYear"/"BySlug"/"Async" qualifier.
        foreach (var suf in new[] { "ForYearAsync", "ForYear", "BySlugAsync", "BySlug", "ById", "Async" })
            if (n.EndsWith(suf, StringComparison.Ordinal) && n.Length > suf.Length) n = n[..^suf.Length];
        return n;
    }
```

Note on `FactToken`: keep it permissive — coverage is best-effort and never scored. For `GetMembersAsync` -> "Members" (matches `...Members[]...`); `GetNameLockDateAsync` -> "NameLockDate" (matches settings path); `GetBySlugAsync` -> "" after stripping (returns CampInfo -> uncovered); `BuildCampDetailAsync` -> "CampDetail".

- [ ] **Step 4: Run -> PASS** all four; full suite green.
- [ ] **Step 5: Commit** `feat(baseline): conservation gate (helper-first, coverage check, 3 verdict kinds + evidence)`

---

## Task 4: JSON `conservationEvidence` + DTO-growth exemption

**Files:**
- Modify: `src/Reforge/Commands/SurfaceScoreCommand.cs` (`WriteJson` payload)
- Test: `test/Reforge.Tests/SurfaceScoreBaselineTests.cs`

- [ ] **Step 1: Failing test** - assert that a canonical-consolidation that also grows the DTO does NOT additionally produce a `parameter-bag-consolidation` / DTO-bloat suspicious entry (item 3 exemption). Since the gate only emits its own kinds and the existing flags key off `methodParameterOverflow`/param-bag rules (not DTO growth), this is a guard test:

```csharp
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
```

- [ ] **Step 2: Run -> FAIL or PASS.** If it already passes (the existing flags don't key off DTO growth), keep it as a regression guard and proceed. If a stray flag appears, the section's param-bag delta must be zero in the synthetic baseline (it is) — adjust the fixture, not the engine.
- [ ] **Step 3: Implement JSON.** In `WriteJson`, after `helperCandidates`:

```csharp
            conservationEvidence = baseline is null ? Array.Empty<object>() : baseline.ConservationVerdicts.Select(v => new
            {
                section = v.Section, kind = v.Kind, improvement = v.Improvement, message = v.Message,
                methods = v.Methods.Select(m => new
                {
                    removedMethod = m.RemovedMethod, coverageKind = m.CoverageKind, targetDto = m.TargetDto,
                    coveredBy = m.CoveredBy, missingInfoFacts = m.MissingInfoFacts.Select(f => new { fact = f.Fact, targetDto = f.TargetDto }).ToArray()
                }).ToArray()
            }).ToArray(),
```

Also surface the `baseline-anchors-missing` precision diagnostic in the command after `baseline = SurfaceScoreBaseline.Compare(...)` (SurfaceScoreCommand.cs ~L112):

```csharp
                        if (baseline.BaselineAnchorsMissing)
                            report.Diagnostics.Add(new ScoreDiagnostic("info", "baseline-anchors-missing",
                                "Baseline JSON predates conservationAnchors (v0.19+); conservation coverage degraded to ambiguous."));
```

- [ ] **Step 4: Run -> PASS**; full suite green.
- [ ] **Step 5: Commit** `feat(surface-score): conservationEvidence JSON + baseline-anchors-missing diagnostic`

---

## Task 5: Fixtures + acceptance test through the engine

Add the static helper fixture (used by Task 1 + the helper-detection path), and one end-to-end test that scores the sample, writes its own JSON as a baseline, deletes a method via a second config-less pass, and asserts a verdict. Because the sample is static, the realistic end-to-end test is: score the sample twice with configs that differ in which read methods are in-section, proving the gate runs through real anchors.

**Files:**
- Modify: `test/SampleSolution/SampleSolution.Services/CampFixtures.cs`
- Test: `test/Reforge.Tests/SurfaceScoreBaselineTests.cs` (or `SectionArchitectureTests.cs`)

- [ ] **Step 1: Fixture** - append to `CampFixtures.cs`:

```csharp
// Static stateless helper - a helper-extraction sink the conservation gate must detect.
public static class CampReadModelProjection
{
    public static string BuildCampDetail(CampInfo info) => info.Name;
    public static bool IsUserCampLead(CampInfo info, Guid userId) => false;
}
```

- [ ] **Step 2: Test** - an end-to-end gate run using the real sample anchors as the "current", and a hand-authored baseline that has one extra removed read method whose fact is on real `CampInfo` paths:

```csharp
[Fact]
public async Task Gate_EndToEnd_RealAnchors_ExistingFactConsolidation()
{
    var cfg = SectionArchTestConfig();           // Camp section (see SectionArchitectureTests.CampConfig analog)
    var engine = new SurfaceScoreEngine(cfg, LocationHelper.GetSolutionDirectory(_fixture.Solution));
    var now = await engine.ScoreAsync(_fixture.Solution, CancellationToken.None);

    // Baseline = current anchors + an EXTRA removed read method "GetSeasonMembersAsync" whose
    // fact "Members" is already on CampInfo.Seasons[].Members[...] -> existingDtoFact.
    var baseline = BuildBaselineJsonFrom(now, addRemovedMethod: ("GetSeasonMembersAsync", "List<CampMemberInfo>"), section: "Camp");
    var path = Path.GetTempFileName(); File.WriteAllText(path, baseline);
    try
    {
        var cmp = SurfaceScoreBaseline.Compare(now, path);
        var v = cmp.ConservationVerdicts.Single(x => x.Section == "Camp");
        Assert.Equal("canonical-consolidation", v.Kind);
        Assert.Contains(v.Methods, m => m.RemovedMethod == "GetSeasonMembersAsync" && m.CoverageKind == "existingDtoFact");
    }
    finally { File.Delete(path); }
}
```

Provide `SectionArchTestConfig()` (copy the Camp config builder from `SectionArchitectureTests`, with default weights/classifications merged) and `BuildBaselineJsonFrom(report, addRemovedMethod, section)` (serialize the report's `conservationAnchors` + `groups` to the baseline shape, then append the extra method to the section's `readServiceInterface` anchor and bump its `readServiceInterfaceMethod` group points by 6 so read surface "dropped").

- [ ] **Step 3: Run -> PASS**; full suite green. Fix any disturbed counts from the new static fixture (it adds `applicationServiceMethod`-style points? No - static class methods are not classified as a service; if any count breaks, update it).
- [ ] **Step 4: Commit** `test(baseline): end-to-end conservation gate through real sample anchors + helper fixture`

---

## Task 6: Glossary note + CHANGELOG + dogfood + ship v0.20.0

- [ ] **Glossary:** no new scored rules in Plan C (the gate emits `SuspiciousImprovement` kinds, not scored rules), so `SurfaceScoreRuleGlossary` is unchanged. Confirm the `RuleGlossary_DescriptionsAreFactualNotAdvisory` test still passes.
- [ ] **CHANGELOG.md:** prepend `## v0.20.0 - conservation gate` (what + why; ASCII only): the three verdict kinds, helper-first ordering, coverage against conservationAnchors inventories, per-method evidence, helperCandidates, baseline-anchors-missing fallback.
- [ ] **Dogfood (built DLL, memory [[reference_dogfooding]]):**
  - `dotnet build Reforge.slnx`
  - Sample two-pass: score the sample with a temp Camp config (`reforge.surface-score.json` beside the slnx) to JSON as `/tmp/base.json`; hand-edit a copy removing one read method's anchor entry to simulate a prior state OR just run `surface-score --baseline /tmp/base.json` against the same state (verdict: neutral, no conservation verdicts) to confirm no crash + `conservationEvidence` key present. Then `rm -f` the temp config.
  - Humans (`H:/source/humans/Humans.slnx`, `timeout 480`): produce a baseline JSON, then re-run with `--baseline` and inspect `.conservationEvidence` + the new `suspiciousImprovements` kinds. Note build.degraded (issue #9). Report concrete numbers (how many sections got each verdict kind).
  - Camps PR branch: `gh pr list --repo peterdrier/Humans`; no dedicated Camps PR exists as of this writing -> fall back to Humans main, documenting the gap (real before/after lives on a refactor branch when one is open).
- [ ] **Ship (locked decision, [[project_b_c_run]]):** commit features; SEPARATE csproj `<Version>0.20.0</Version>` bump commit; `git push origin main`; `dotnet pack src/Reforge/Reforge.csproj -c Release -o src/Reforge/nupkg`; `dotnet tool update --global --add-source src/Reforge/nupkg Reforge --version 0.20.0`; verify `reforge --version`.
- [ ] One final summary; update memory ([[feedback_scoring_design]]) with the B/C principles (gate-not-score for unprovable trades, helper-first ordering, coverage-against-inventories-not-deltas).

---

## Self-review notes

- **Spec coverage:** Section 4 decision tree (Task 3: helper-first, coverage check, 3 kinds, ambiguity bias); per-method evidence rows with `coverageKind`+`targetDto` (Task 3 `MethodEvidence`); `conservationAnchors` consumed from JSON not deltas (Task 2/3); `baseline-anchors-missing` fallback (Task 2/4); helper detection broadened to any new stateless sink (Task 1 `BuildHelperCandidates`); settings-vs-primary targetDto split (Task 3 `Cover`); item 3 DTO-growth exemption (Task 4 guard); item 6 helper-before-ambiguity regression guard (Task 3 `Gate_NewHelperAbsorbsRemoved_BeatsAmbiguity`). Advisory `missingInfoFacts` emitted on ambiguous/uncovered (Task 3).
- **Not scored:** the gate adds zero scored rules; it only emits `SuspiciousImprovement` (improvement verdicts) + evidence. No new gameable reward term (principle 3). Coverage is best-effort and explicitly never scored.
- **Behavioral, not nominal:** removed-method detection is by interface-anchor diff; coverage is by inventory-path matching; helper detection is by stateless-shape + method-name/body. Names are tie-breakers only.
- **Type consistency:** `HelperCandidate(Display, Methods)`, `MethodEvidence(RemovedMethod, CoverageKind, TargetDto, CoveredBy, MissingInfoFacts)`, `ConservationVerdict(Section, Kind, Improvement, Message, Methods)` fixed in the Data model and used unchanged in Tasks 3-5. Reuses `MissingInfoFact` from Plan B.
- **Risk:** coverage matching is fuzzy (token-substring). Mitigated by: it is never scored; ambiguity leans consolidation but is surfaced; helper-first ordering prevents the laundering hole; tests pin each branch with crafted fixtures.
