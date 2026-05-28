# Section-Architecture Scoring - Plan A (Foundation) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the shared foundation for section-architecture scoring: config metadata on `SectionRule`, a reusable `SolutionClassifier`, a behavioral read-method classifier, and a recursive path-based DTO inventory. No command output changes; pure infrastructure consumed by Plans B and C.

**Architecture:** Extract the type-classification pass out of `SurfaceScoreEngine` into a standalone `SolutionClassifier` (behavior-preserving; existing 70 tests are the guard) so `surface-score`, the future `section-shape` command, and the conservation gate all share one classification. Add new pure-logic helpers (`ReadSurface`, `DtoInventory`) with their own unit tests. Extend `SectionRule` with section metadata, parsed from `reforge.surface-score.json`.

**Tech Stack:** .NET 10, Microsoft.CodeAnalysis (Roslyn), System.Text.Json, xUnit. Build: `dotnet build Reforge.slnx`. Test: `dotnet test Reforge.slnx`. Shell is Windows Git Bash; never chain `cd x && cmd` (hook-blocked) - `cd` in one Bash call, run in the next.

**Spec:** `docs/superpowers/specs/2026-05-29-section-architecture-scoring-design.md` (Sections 1 and the shared pieces of 3-5).

---

## File Structure

- **Modify** `src/Reforge/SurfaceScoreConfig.cs` - add metadata fields + record types on `SectionRule`.
- **Create** `src/Reforge/SolutionClassifier.cs` - the classification pass moved out of the engine; produces `IReadOnlyList<ClassifiedType>` + a top-level `ClassifiedType` record.
- **Modify** `src/Reforge/SurfaceScoreEngine.cs` - delete the moved methods + the inline classification loop; call `SolutionClassifier` instead. `BuildFullToReadPairs` made `internal static` for reuse.
- **Create** `src/Reforge/ReadSurface.cs` - `ReadMethodKind` enum + behavioral classifier.
- **Create** `src/Reforge/DtoInventory.cs` - recursive path-based member inventory.
- **Modify** `test/Reforge.Tests/SurfaceScoreTests.cs` - one new config-parse test.
- **Create** `test/Reforge.Tests/SolutionClassifierTests.cs` - classifier-equivalence + repoBacked tests.
- **Create** `test/Reforge.Tests/ReadSurfaceTests.cs` - one test per read-method kind + the search-shape gate.
- **Create** `test/Reforge.Tests/DtoInventoryTests.cs` - nested/collection path + cycle-bound tests.
- **Modify** `test/SampleSolution/SampleSolution.Services/CampFixtures.cs` - add a read/full pair + nested Info DTO used by the inventory and classifier tests.

---

## Task 1: Extend `SectionRule` with section metadata

**Files:**
- Modify: `src/Reforge/SurfaceScoreConfig.cs` (the `SectionRule` class, ends ~line 290)
- Test: `test/Reforge.Tests/SurfaceScoreTests.cs`

- [ ] **Step 1: Write the failing test**

Add to `test/Reforge.Tests/SurfaceScoreTests.cs`:

```csharp
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
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Reforge.slnx --filter "FullyQualifiedName~LoadOrDefault_ParsesSectionMetadata"`
Expected: FAIL to compile - `PrimaryInfoDto` etc. do not exist on `SectionRule`.

- [ ] **Step 3: Add the fields + record types**

In `src/Reforge/SurfaceScoreConfig.cs`, add these fields to `SectionRule` (after `CanonicalReadDtos`, before the closing brace ~line 290):

```csharp
    /// <summary>Primary read/cache DTO for this section. Default convention: "&lt;Section&gt;Info".</summary>
    public string? PrimaryInfoDto { get; set; }
    /// <summary>Settings DTO for this section. Default convention: "&lt;Section&gt;SettingsInfo".</summary>
    public string? SettingsInfoDto { get; set; }
    /// <summary>Cache value DTO. Default: == PrimaryInfoDto; else inferred from a caching decorator.</summary>
    public string? CacheDto { get; set; }
    /// <summary>Documented read shards (narrow read models intentionally split off the primary read surface).</summary>
    public List<ReadShard> ReadShards { get; set; } = new();
    /// <summary>Override: does this section require a read surface? Null = inferred from repo-backed.</summary>
    public bool? RequiresReadSurface { get; set; }
    /// <summary>Override: does this section require a write/full surface? Null = inferred from repo-backed.</summary>
    public bool? RequiresWriteSurface { get; set; }
    /// <summary>Override: does this section require a primary Info DTO? Null = inferred from repo-backed.</summary>
    public bool? RequiresPrimaryInfoDto { get; set; }
    /// <summary>Cross-section write/full dependencies exempt from crossSectionWriteSurface (visible debt).</summary>
    public List<GrandfatheredDependency> GrandfatheredDependencies { get; set; } = new();
    /// <summary>Read methods exempt from readSurfaceProjectionMethod (visible debt).</summary>
    public List<EscapeHatchReadMethod> EscapeHatchReadMethods { get; set; } = new();

    /// <summary>True when the section owns at least one repository by config. The engine widens this
    /// with classified repositories resolved into the section (see SolutionClassifier).</summary>
    public bool HasConfiguredRepository => RepositoryInterfaces.Count > 0;
```

Then add these record types at the end of the file (after the `SectionRule` class):

```csharp
public sealed class ReadShard
{
    public string Name { get; set; } = "";
    public string Purpose { get; set; } = "";
}

public sealed class GrandfatheredDependency
{
    /// <summary>"CallerType" or "CallerType-&gt;ICalleeInterface".</summary>
    public string Dependency { get; set; } = "";
    public string Reason { get; set; } = "";
    public string Since { get; set; } = "";
    public string? Owner { get; set; }
}

public sealed class EscapeHatchReadMethod
{
    /// <summary>Glob over "Interface.Method" or "Method".</summary>
    public string Method { get; set; } = "";
    public string Reason { get; set; } = "";
    public string Since { get; set; } = "";
    public string? Owner { get; set; }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test Reforge.slnx --filter "FullyQualifiedName~LoadOrDefault_ParsesSectionMetadata"`
Expected: PASS.

- [ ] **Step 5: Run the full suite to confirm no regressions**

Run: `dotnet test Reforge.slnx`
Expected: all existing tests + the new one PASS.

- [ ] **Step 6: Commit**

```bash
git add src/Reforge/SurfaceScoreConfig.cs test/Reforge.Tests/SurfaceScoreTests.cs
git commit -m "feat(surface-score): add section metadata fields to SectionRule"
```

---

## Task 2: Extract `SolutionClassifier` and `ClassifiedType` from the engine

This is a behavior-preserving move. The existing 70 tests are the guard - no test should change.

**Files:**
- Create: `src/Reforge/SolutionClassifier.cs`
- Modify: `src/Reforge/SurfaceScoreEngine.cs` (remove moved members; call the classifier)
- Test: `test/Reforge.Tests/SolutionClassifierTests.cs`

- [ ] **Step 1: Write the failing test**

Create `test/Reforge.Tests/SolutionClassifierTests.cs`:

```csharp
namespace Reforge.Tests;

[Collection("SampleSolution")]
public class SolutionClassifierTests
{
    private readonly SampleSolutionFixture _fixture;
    public SolutionClassifierTests(SampleSolutionFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task ClassifyAsync_TagsKnownTypes()
    {
        var cfg = SurfaceScoreConfig.Default();
        var classified = await SolutionClassifier.ClassifyAsync(
            _fixture.Solution, cfg, LocationHelper.GetSolutionDirectory(_fixture.Solution), CancellationToken.None);

        // UserService is an application service; IUserService a full-service interface.
        Assert.Contains(classified, c => c.Type.Name == "UserService" && c.Tags.Contains("applicationService"));
        Assert.Contains(classified, c => c.Type.Name == "IUserService" && c.Tags.Contains("fullServiceInterface"));
        // No duplicate ToDisplayString entries (cross-project dedup preserved).
        Assert.Equal(classified.Select(c => c.Type.ToDisplayString()).Distinct().Count(), classified.Count);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Reforge.slnx --filter "FullyQualifiedName~ClassifyAsync_TagsKnownTypes"`
Expected: FAIL to compile - `SolutionClassifier` does not exist.

- [ ] **Step 3: Create `SolutionClassifier.cs`**

Move the type enumeration, `ResolveSection`, `SectionMatchKind`, `SectionMatchResult`, `Classify`, `Matches`, `InheritsByName`, and the classification loop out of `SurfaceScoreEngine.cs` into this new file. `ClassifiedType` becomes a top-level `public sealed record`. Create `src/Reforge/SolutionClassifier.cs`:

```csharp
using Microsoft.CodeAnalysis;

namespace Reforge;

/// <summary>
/// The shared type-classification pass: opens each non-test project, enumerates source-declared
/// types, resolves each into a section (group) and assigns role tags. Extracted from
/// SurfaceScoreEngine so surface-score, section-shape, and the baseline gate share one pass.
/// </summary>
public static class SolutionClassifier
{
    public static async Task<IReadOnlyList<ClassifiedType>> ClassifyAsync(
        Solution solution, SurfaceScoreConfig config, string solutionDirectory, CancellationToken ct)
    {
        var classified = new List<ClassifiedType>();
        var seenByDisplay = new HashSet<string>(StringComparer.Ordinal);

        foreach (var project in solution.Projects)
        {
            if (project.Name.Contains("Test", StringComparison.OrdinalIgnoreCase)) continue;
            var compilation = await project.GetCompilationAsync(ct);
            if (compilation is null) continue;

            foreach (var type in EnumerateTypes(compilation.GlobalNamespace))
            {
                if (!type.Locations.Any(l => l.IsInSource)) continue;
                if (type.IsImplicitlyDeclared) continue;
                if (type.DeclaredAccessibility == Accessibility.Private) continue;
                if (!seenByDisplay.Add(type.ToDisplayString())) continue;

                var primaryLocation = type.Locations.First(l => l.IsInSource);
                var filePath = primaryLocation.SourceTree?.FilePath ?? "";
                var relPath = LocationHelper.NormalizePath(filePath, solutionDirectory);
                var nsName = type.ContainingNamespace?.ToDisplayString() ?? "";

                var (group, sectionMatch) = ResolveSection(config, type, relPath, nsName);
                var tags = Classify(config, type, relPath, nsName);

                if (sectionMatch?.MatchKind == SectionMatchKind.RepositoryInterface)
                {
                    tags.Add("repositoryInterface");
                    tags.Remove("fullServiceInterface");
                    tags.Remove("readServiceInterface");
                }
                else if (sectionMatch?.MatchKind == SectionMatchKind.ReadServiceInterface)
                {
                    tags.Add("readServiceInterface");
                    tags.Remove("fullServiceInterface");
                    tags.Remove("repositoryInterface");
                }
                else if (sectionMatch?.MatchKind == SectionMatchKind.ServiceInterface)
                {
                    tags.Add("fullServiceInterface");
                    tags.Remove("readServiceInterface");
                    tags.Remove("repositoryInterface");
                }

                classified.Add(new ClassifiedType(type, group, tags, relPath, primaryLocation));
            }
        }

        return classified;
    }

    private static IEnumerable<INamedTypeSymbol> EnumerateTypes(INamespaceSymbol ns)
    {
        foreach (var m in ns.GetMembers())
        {
            switch (m)
            {
                case INamespaceSymbol child:
                    foreach (var t in EnumerateTypes(child)) yield return t;
                    break;
                case INamedTypeSymbol type:
                    yield return type;
                    foreach (var nested in type.GetTypeMembers())
                        yield return nested;
                    break;
            }
        }
    }

    private static (string Group, SectionMatchResult? MatchResult) ResolveSection(
        SurfaceScoreConfig config, INamedTypeSymbol type, string filePath, string namespaceName)
    {
        foreach (var rule in config.EffectiveSections)
        {
            if (rule.RepositoryInterfaces.Contains(type.Name, StringComparer.Ordinal))
                return (rule.Name, new SectionMatchResult(SectionMatchKind.RepositoryInterface));
            if (rule.ReadServiceInterfaces.Contains(type.Name, StringComparer.Ordinal))
                return (rule.Name, new SectionMatchResult(SectionMatchKind.ReadServiceInterface));
            if (rule.ServiceInterfaces.Contains(type.Name, StringComparer.Ordinal))
                return (rule.Name, new SectionMatchResult(SectionMatchKind.ServiceInterface));

            foreach (var p in rule.Paths)
                if (GlobMatcher.MatchesPath(filePath, p))
                    return (rule.Name, new SectionMatchResult(SectionMatchKind.Path));
            foreach (var n in rule.Namespaces)
                if (namespaceName.StartsWith(n, StringComparison.Ordinal))
                    return (rule.Name, new SectionMatchResult(SectionMatchKind.Namespace));
            foreach (var s in rule.Symbols)
                if (GlobMatcher.MatchesName(type.Name, s))
                    return (rule.Name, new SectionMatchResult(SectionMatchKind.Symbol));
        }

        if (config.GroupByNamespaceFallback && !string.IsNullOrEmpty(namespaceName))
        {
            var parts = namespaceName.Split('.');
            if (parts.Length >= 3) return (parts[2], null);
            if (parts.Length >= 2) return (parts[1], null);
            return (parts[0], null);
        }

        return ("(ungrouped)", null);
    }

    internal enum SectionMatchKind { Path, Namespace, Symbol, RepositoryInterface, ServiceInterface, ReadServiceInterface }
    internal sealed record SectionMatchResult(SectionMatchKind MatchKind);

    private static HashSet<string> Classify(SurfaceScoreConfig config, INamedTypeSymbol type, string filePath, string namespaceName)
    {
        var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, rule) in config.Classifications)
            if (Matches(rule, type, filePath, namespaceName))
                tags.Add(name);

        if (type.TypeKind == TypeKind.Interface)
        {
            tags.Remove("repositoryImplementation");
            tags.Remove("applicationService");
            tags.Remove("controller");
            tags.Remove("backgroundJob");
            if (tags.Contains("readServiceInterface")) tags.Remove("fullServiceInterface");
            if (tags.Contains("repositoryInterface")) tags.Remove("fullServiceInterface");
        }
        else
        {
            tags.Remove("readServiceInterface");
            tags.Remove("fullServiceInterface");
            tags.Remove("repositoryInterface");
            if (tags.Contains("repositoryImplementation")) tags.Remove("applicationService");
        }
        return tags;
    }

    private static bool Matches(ClassificationRule rule, INamedTypeSymbol type, string filePath, string namespaceName)
    {
        foreach (var p in rule.NamePatterns)
            if (GlobMatcher.MatchesName(type.Name, p)) return true;
        foreach (var p in rule.Paths)
            if (GlobMatcher.MatchesPath(filePath, p)) return true;
        foreach (var n in rule.Namespaces)
            if (namespaceName.StartsWith(n, StringComparison.Ordinal)) return true;
        foreach (var i in rule.Inherits)
            if (InheritsByName(type, i)) return true;
        foreach (var a in rule.AttributeNames)
            if (type.GetAttributes().Any(at => at.AttributeClass?.Name == a || at.AttributeClass?.Name == a + "Attribute")) return true;
        return false;
    }

    private static bool InheritsByName(INamedTypeSymbol type, string name)
    {
        var current = type.BaseType;
        while (current is not null)
        {
            if (current.Name == name) return true;
            current = current.BaseType;
        }
        foreach (var iface in type.AllInterfaces)
            if (iface.Name == name) return true;
        return false;
    }
}

/// <summary>A source type with its resolved section group, role tags, and primary location.</summary>
public sealed record ClassifiedType(
    INamedTypeSymbol Type,
    string Group,
    HashSet<string> Tags,
    string File,
    Location PrimaryLocation)
{
    public int Line => PrimaryLocation.GetLineSpan().StartLinePosition.Line + 1;
}
```

- [ ] **Step 4: Rewire the engine to use the classifier**

In `src/Reforge/SurfaceScoreEngine.cs`:

1. Delete the nested `private sealed record ClassifiedType(...)` (it is now top-level in `SolutionClassifier.cs`).
2. Delete `EnumerateTypes`, `ResolveSection`, the nested `SectionMatchKind`/`SectionMatchResult`, `Classify`, `Matches`, `InheritsByName` (now in `SolutionClassifier`).
3. In `ScoreAsync`, replace the entire `classified`-building block (the `var classified = new List<ClassifiedType>(); var seenByDisplay = ...; foreach (var project ...) { ... }` from just after `report.ConfiguredSections.AddRange(...)` down to the `report.TypesAnalyzed = classified.Count;` line) with:

```csharp
        var classified = (await SolutionClassifier.ClassifyAsync(solution, _config, _solutionDirectory, ct)).ToList();
        report.TypesAnalyzed = classified.Count;
```

4. Change `BuildFullToReadPairs` from `private` to `internal static`, and have it take its inputs as parameters (it currently reads no instance state except via args). Its signature becomes:

```csharp
    internal static Dictionary<string, ClassifiedType> BuildFullToReadPairs(
        List<ClassifiedType> classified,
        Dictionary<string, ClassifiedType> typesByDisplay)
```

(The body is unchanged; it already uses only its parameters.)

- [ ] **Step 5: Run the full suite (regression guard)**

Run: `dotnet test Reforge.slnx`
Expected: ALL existing 70 tests PASS unchanged, plus `ClassifyAsync_TagsKnownTypes`. If any existing surface-score test fails, the move changed behavior - diff the moved code against the original and fix.

- [ ] **Step 6: Commit**

```bash
git add src/Reforge/SolutionClassifier.cs src/Reforge/SurfaceScoreEngine.cs test/Reforge.Tests/SolutionClassifierTests.cs
git commit -m "refactor(surface-score): extract SolutionClassifier from the engine"
```

---

## Task 3: Repo-backed inference + `requiresX` resolution

A section is repo-backed when it owns a repository by config (`HasConfiguredRepository`) OR a classified `repositoryInterface`/`repositoryImplementation` resolved into the section. `requiresReadSurface`/`requiresWriteSurface`/`requiresPrimaryInfoDto` default to repo-backed unless overridden.

**Files:**
- Create: `src/Reforge/SectionFacts.cs`
- Test: `test/Reforge.Tests/SolutionClassifierTests.cs` (add cases)

- [ ] **Step 1: Write the failing test**

Add to `SolutionClassifierTests.cs`:

```csharp
[Fact]
public void SectionFacts_RepoBacked_FromConfiguredRepository()
{
    var rule = new SectionRule { Name = "Camp", RepositoryInterfaces = { "ICampRepository" } };
    var facts = SectionFacts.For(rule, classifiedRepoSectionNames: new HashSet<string>(StringComparer.OrdinalIgnoreCase));
    Assert.True(facts.RepoBacked);
    Assert.True(facts.RequiresReadSurface);     // defaults to repo-backed
    Assert.True(facts.RequiresWriteSurface);
    Assert.True(facts.RequiresPrimaryInfoDto);
}

[Fact]
public void SectionFacts_OrchestratorOnly_NotRequired()
{
    var rule = new SectionRule { Name = "Orchestrator" };
    var facts = SectionFacts.For(rule, classifiedRepoSectionNames: new HashSet<string>(StringComparer.OrdinalIgnoreCase));
    Assert.False(facts.RepoBacked);
    Assert.False(facts.RequiresReadSurface);
}

[Fact]
public void SectionFacts_RequiresOverride_Wins()
{
    var rule = new SectionRule { Name = "Orchestrator", RequiresReadSurface = true };
    var facts = SectionFacts.For(rule, classifiedRepoSectionNames: new HashSet<string>(StringComparer.OrdinalIgnoreCase));
    Assert.False(facts.RepoBacked);
    Assert.True(facts.RequiresReadSurface);     // explicit override beats inference
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Reforge.slnx --filter "FullyQualifiedName~SectionFacts"`
Expected: FAIL to compile - `SectionFacts` does not exist.

- [ ] **Step 3: Create `SectionFacts.cs`**

```csharp
namespace Reforge;

/// <summary>
/// Resolved per-section architectural expectations. RepoBacked is inferred (config repository
/// OR a classified repository resolved into the section); the requiresX flags default to
/// RepoBacked unless the config overrides them.
/// </summary>
public sealed record SectionFacts(
    string Name,
    bool RepoBacked,
    bool RequiresReadSurface,
    bool RequiresWriteSurface,
    bool RequiresPrimaryInfoDto)
{
    public static SectionFacts For(SectionRule rule, IReadOnlySet<string> classifiedRepoSectionNames)
    {
        bool repoBacked = rule.HasConfiguredRepository
            || classifiedRepoSectionNames.Contains(rule.Name);
        return new SectionFacts(
            rule.Name,
            repoBacked,
            rule.RequiresReadSurface ?? repoBacked,
            rule.RequiresWriteSurface ?? repoBacked,
            rule.RequiresPrimaryInfoDto ?? repoBacked);
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test Reforge.slnx --filter "FullyQualifiedName~SectionFacts"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Reforge/SectionFacts.cs test/Reforge.Tests/SolutionClassifierTests.cs
git commit -m "feat(surface-score): add repo-backed SectionFacts inference"
```

---

## Task 4: Behavioral read-method classifier (`ReadSurface`)

Classifies a read-interface method into one kind, behaviorally. Used by `readSurfaceProjectionMethod` (Plan B) and `derivableReadMethods` (advisory). Search is "healthy" only with a real search shape.

**Files:**
- Create: `src/Reforge/ReadSurface.cs`
- Test: `test/Reforge.Tests/ReadSurfaceTests.cs`

- [ ] **Step 1: Write the failing test**

Create `test/Reforge.Tests/ReadSurfaceTests.cs`:

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Reforge.Tests;

public class ReadSurfaceTests
{
    private static IMethodSymbol Method(string iface, string member)
    {
        var tree = CSharpSyntaxTree.ParseText($$"""
            using System; using System.Threading.Tasks; using System.Collections.Generic;
            public sealed class CampInfo { public Guid Id { get; set; } }
            public sealed class CampSettingsInfo { public int Year { get; set; } }
            public sealed class CampSummary { public string Name { get; set; } }
            public sealed class CampSearchHit { public Guid Id { get; set; } }
            public sealed class CampSearchQuery { public string Term { get; set; } public int Page { get; set; } }
            public interface {{iface}} { {{member}} }
            """);
        var comp = CSharpCompilation.Create("t", new[] { tree },
            new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) });
        var sym = comp.GetTypeByMetadataName(iface)!;
        return sym.GetMembers().OfType<IMethodSymbol>().First(m => m.MethodKind == MethodKind.Ordinary);
    }

    [Theory]
    [InlineData("Task<bool> IsUserCampLeadAsync(Guid u);", ReadMethodKind.Predicate)]
    [InlineData("Task<Guid> GetCampLeadSeasonIdAsync(Guid c);", ReadMethodKind.ScalarFact)]
    [InlineData("Task<List<CampSummary>> GetCampSummariesForYearAsync(int y);", ReadMethodKind.ProjectionSummary)]
    [InlineData("Task<CampInfo> GetByIdAsync(Guid id);", ReadMethodKind.PrimitiveRead)]
    [InlineData("Task<CampSettingsInfo> GetSettingsAsync(Guid c);", ReadMethodKind.SettingsRead)]
    [InlineData("Task<List<CampSearchHit>> SearchAsync(CampSearchQuery q);", ReadMethodKind.Search)]
    public void Classify_AssignsExpectedKind(string member, ReadMethodKind expected)
    {
        var m = Method("ICampServiceRead", member);
        var kind = ReadSurface.Classify(m, primaryInfoDto: "CampInfo", settingsInfoDto: "CampSettingsInfo");
        Assert.Equal(expected, kind);
    }

    [Fact]
    public void Classify_SearchNamedButWrongShape_IsProjectionNotSearch()
    {
        // Named Search*, takes a string, but returns a single projection - NOT a healthy search.
        var m = Method("ICampServiceRead", "Task<CampSummary> SearchOneAsync(string term);");
        var kind = ReadSurface.Classify(m, "CampInfo", "CampSettingsInfo");
        Assert.Equal(ReadMethodKind.ProjectionSummary, kind);
    }

    [Fact]
    public void IsCharged_TrueForProjectionPredicateScalarUiBuilder()
    {
        Assert.True(ReadSurface.IsCharged(ReadMethodKind.ProjectionSummary));
        Assert.True(ReadSurface.IsCharged(ReadMethodKind.Predicate));
        Assert.True(ReadSurface.IsCharged(ReadMethodKind.ScalarFact));
        Assert.True(ReadSurface.IsCharged(ReadMethodKind.UiBuilder));
        Assert.False(ReadSurface.IsCharged(ReadMethodKind.PrimitiveRead));
        Assert.False(ReadSurface.IsCharged(ReadMethodKind.SettingsRead));
        Assert.False(ReadSurface.IsCharged(ReadMethodKind.Search));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Reforge.slnx --filter "FullyQualifiedName~ReadSurfaceTests"`
Expected: FAIL to compile - `ReadSurface`/`ReadMethodKind` do not exist.

- [ ] **Step 3: Create `ReadSurface.cs`**

```csharp
using Microsoft.CodeAnalysis;

namespace Reforge;

public enum ReadMethodKind
{
    PrimitiveRead,    // returns primary Info DTO (or collection of it) - healthy
    SettingsRead,     // returns settings DTO - healthy
    Search,           // real search shape (query/paging input + search-hit/result output) - healthy
    ProjectionSummary,// returns a non-primary DTO / collection - charged
    Predicate,        // returns bool - charged
    ScalarFact,       // returns a single primitive/string/Guid/DateTime - charged
    UiBuilder         // returns a composed view DTO (*Data/*ViewModel/*PageModel) - charged
}

/// <summary>
/// Behavioral classifier for read-service-interface methods. Decides by return/parameter shape,
/// not by method name alone - renaming a projection to Search* must not make it "healthy".
/// </summary>
public static class ReadSurface
{
    public static bool IsCharged(ReadMethodKind k) =>
        k is ReadMethodKind.ProjectionSummary or ReadMethodKind.Predicate
          or ReadMethodKind.ScalarFact or ReadMethodKind.UiBuilder;

    public static ReadMethodKind Classify(IMethodSymbol m, string? primaryInfoDto, string? settingsInfoDto)
    {
        var ret = UnwrapTaskLike(m.ReturnType);

        // Predicate: returns bool.
        if (ret.SpecialType == SpecialType.System_Boolean) return ReadMethodKind.Predicate;

        var element = UnwrapCollection(ret);

        // ScalarFact: single primitive/string/Guid/DateTime (not a collection of them).
        if (ReferenceEquals(element, ret) && IsScalarFact(ret)) return ReadMethodKind.ScalarFact;

        var elementName = (element as INamedTypeSymbol)?.Name;

        // PrimitiveRead / SettingsRead: returns the configured canonical DTOs.
        if (elementName is not null && primaryInfoDto is not null && elementName == primaryInfoDto)
            return ReadMethodKind.PrimitiveRead;
        if (elementName is not null && settingsInfoDto is not null && elementName == settingsInfoDto)
            return ReadMethodKind.SettingsRead;

        // Search: requires BOTH a query/filter/paging-shaped input AND a search-hit/result output.
        if (HasSearchInput(m) && IsSearchResult(element)) return ReadMethodKind.Search;

        // UiBuilder: composed view DTO by name suffix.
        if (elementName is not null &&
            (elementName.EndsWith("Data", StringComparison.Ordinal)
             || elementName.EndsWith("ViewModel", StringComparison.Ordinal)
             || elementName.EndsWith("PageModel", StringComparison.Ordinal)))
            return ReadMethodKind.UiBuilder;

        // Everything else returning a DTO is a projection-summary.
        return ReadMethodKind.ProjectionSummary;
    }

    private static bool IsScalarFact(ITypeSymbol t)
    {
        if (t.SpecialType is SpecialType.System_String or SpecialType.System_Int32
            or SpecialType.System_Int64 or SpecialType.System_Boolean or SpecialType.System_Double
            or SpecialType.System_Decimal) return true;
        var n = t.Name;
        return n is "Guid" or "DateTime" or "DateTimeOffset" or "DateOnly" or "TimeOnly";
    }

    private static bool HasSearchInput(IMethodSymbol m)
    {
        foreach (var p in m.Parameters)
        {
            if (p.Type.Name == "CancellationToken") continue;
            var tn = p.Type.Name;
            if (tn.EndsWith("Query", StringComparison.Ordinal)
                || tn.EndsWith("Filter", StringComparison.Ordinal)
                || tn.EndsWith("SearchRequest", StringComparison.Ordinal)
                || tn.EndsWith("Criteria", StringComparison.Ordinal)
                || p.Name is "page" or "pageSize" or "skip" or "take")
                return true;
        }
        return false;
    }

    private static bool IsSearchResult(ITypeSymbol element)
    {
        var n = (element as INamedTypeSymbol)?.Name ?? element.Name;
        return n.EndsWith("SearchHit", StringComparison.Ordinal)
            || n.EndsWith("Hit", StringComparison.Ordinal)
            || n.EndsWith("SearchResult", StringComparison.Ordinal)
            || n.EndsWith("Result", StringComparison.Ordinal)
            || n.EndsWith("Page", StringComparison.Ordinal);
    }

    private static ITypeSymbol UnwrapTaskLike(ITypeSymbol t)
    {
        if (t is INamedTypeSymbol n && n.IsGenericType)
        {
            var name = n.OriginalDefinition.ToDisplayString();
            if (name is "System.Threading.Tasks.Task<TResult>" or "System.Threading.Tasks.ValueTask<TResult>")
                return n.TypeArguments[0];
        }
        return t;
    }

    private static ITypeSymbol UnwrapCollection(ITypeSymbol t)
    {
        if (t is INamedTypeSymbol n && n.IsGenericType && n.TypeArguments.Length == 1)
        {
            var od = n.OriginalDefinition.ToDisplayString();
            if (od.StartsWith("System.Collections.Generic.", StringComparison.Ordinal)
                || od.StartsWith("System.Collections.Immutable.", StringComparison.Ordinal)
                || od == "System.Collections.IEnumerable")
                return n.TypeArguments[0];
        }
        if (t is IArrayTypeSymbol arr) return arr.ElementType;
        return t;
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test Reforge.slnx --filter "FullyQualifiedName~ReadSurfaceTests"`
Expected: PASS (all theory cases + the two facts).

- [ ] **Step 5: Commit**

```bash
git add src/Reforge/ReadSurface.cs test/Reforge.Tests/ReadSurfaceTests.cs
git commit -m "feat(surface-score): add behavioral read-method classifier"
```

---

## Task 5: Recursive path-based DTO inventory (`DtoInventory`)

Walks a DTO's public members, descending through canonical child DTOs and collection elements, producing dotted paths (`CampInfo.Seasons[].Members[].UserId`). Depth-bounded; cycle-guarded.

**Files:**
- Create: `src/Reforge/DtoInventory.cs`
- Test: `test/Reforge.Tests/DtoInventoryTests.cs`

- [ ] **Step 1: Write the failing test**

Create `test/Reforge.Tests/DtoInventoryTests.cs`:

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Reforge.Tests;

public class DtoInventoryTests
{
    private static INamedTypeSymbol Dto(string name) => _comp.Value.GetTypeByMetadataName(name)!;

    private static readonly Lazy<CSharpCompilation> _comp = new(() =>
    {
        var tree = CSharpSyntaxTree.ParseText("""
            using System; using System.Collections.Generic;
            public sealed class CampInfo {
                public Guid Id { get; set; }
                public List<CampSeasonInfo> Seasons { get; set; }
                public CampSeasonInfo CurrentSeason { get; set; }
                public List<string> ImageUrls { get; set; }
            }
            public sealed class CampSeasonInfo {
                public int Year { get; set; }
                public List<CampMemberInfo> Members { get; set; }
                public CampInfo Parent { get; set; }   // cycle back to CampInfo
            }
            public sealed class CampMemberInfo { public Guid UserId { get; set; } }
            """);
        return CSharpCompilation.Create("t", new[] { tree },
            new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) });
    });

    [Fact]
    public void Build_ProducesNestedCollectionPaths()
    {
        var canonical = new HashSet<string>(StringComparer.Ordinal) { "CampInfo", "CampSeasonInfo", "CampMemberInfo" };
        var paths = DtoInventory.Build(Dto("CampInfo"), canonical, maxDepth: 5);

        Assert.Contains("CampInfo.Id", paths);
        Assert.Contains("CampInfo.Seasons[].Year", paths);
        Assert.Contains("CampInfo.Seasons[].Members[].UserId", paths);
        Assert.Contains("CampInfo.CurrentSeason.Year", paths);
        Assert.Contains("CampInfo.ImageUrls[]", paths); // collection of leaf type -> path ends at the collection member
    }

    [Fact]
    public void Build_StopsAtCycle()
    {
        var canonical = new HashSet<string>(StringComparer.Ordinal) { "CampInfo", "CampSeasonInfo", "CampMemberInfo" };
        var paths = DtoInventory.Build(Dto("CampInfo"), canonical, maxDepth: 10);
        // Parent walks back to CampInfo; the visited-set must stop it re-expanding CampInfo's members under Parent.
        Assert.DoesNotContain(paths, p => p.Contains("Parent.Seasons[].Members[].UserId"));
        Assert.Contains("CampInfo.Seasons[].Parent", paths); // the member itself is recorded, not expanded
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Reforge.slnx --filter "FullyQualifiedName~DtoInventoryTests"`
Expected: FAIL to compile - `DtoInventory` does not exist.

- [ ] **Step 3: Create `DtoInventory.cs`**

```csharp
using Microsoft.CodeAnalysis;

namespace Reforge;

/// <summary>
/// Builds a recursive, path-based inventory of a DTO's public readable members. Descends through
/// canonical child DTOs and collection elements (marking collections with "[]"), so the
/// conservation gate can prove facts like CampInfo.Seasons[].Members[].UserId are present.
/// Only canonical/child-DTO types are expanded; primitives and non-canonical types are leaf facts.
/// Depth-bounded and cycle-guarded via a visited-type set on each path.
/// </summary>
public static class DtoInventory
{
    public static IReadOnlyList<string> Build(INamedTypeSymbol root, IReadOnlySet<string> canonicalTypeNames, int maxDepth = 5)
    {
        var paths = new List<string>();
        Walk(root, root.Name, new HashSet<string>(StringComparer.Ordinal) { root.Name }, canonicalTypeNames, paths, maxDepth, 0);
        return paths;
    }

    private static void Walk(INamedTypeSymbol type, string prefix, HashSet<string> visited,
        IReadOnlySet<string> canonical, List<string> paths, int maxDepth, int depth)
    {
        foreach (var prop in type.GetMembers().OfType<IPropertySymbol>())
        {
            if (prop.DeclaredAccessibility != Accessibility.Public) continue;
            if (prop.GetMethod is null) continue;

            var (element, isCollection) = Unwrap(prop.Type);
            string suffix = isCollection ? "[]" : "";
            string path = $"{prefix}.{prop.Name}{suffix}";

            // Record the member path. Expand only canonical child DTOs not yet visited and within depth.
            if (element is INamedTypeSymbol named && canonical.Contains(named.Name)
                && !visited.Contains(named.Name) && depth + 1 < maxDepth)
            {
                var nextVisited = new HashSet<string>(visited, StringComparer.Ordinal) { named.Name };
                Walk(named, path, nextVisited, canonical, paths, maxDepth, depth + 1);
            }
            else
            {
                paths.Add(path);
            }
        }
    }

    private static (ITypeSymbol Element, bool IsCollection) Unwrap(ITypeSymbol t)
    {
        if (t is INamedTypeSymbol n && n.IsGenericType && n.TypeArguments.Length == 1)
        {
            var od = n.OriginalDefinition.ToDisplayString();
            if (od.StartsWith("System.Collections.Generic.", StringComparison.Ordinal)
                || od.StartsWith("System.Collections.Immutable.", StringComparison.Ordinal)
                || od == "System.Collections.IEnumerable")
                return (n.TypeArguments[0], true);
        }
        if (t is IArrayTypeSymbol arr) return (arr.ElementType, true);
        return (t, false);
    }
}
```

Note on the cycle test: when a canonical child is already in `visited`, the `else` branch records the member as a leaf path (e.g. `CampInfo.Seasons[].Parent`) and does not expand it - exactly what the test asserts.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test Reforge.slnx --filter "FullyQualifiedName~DtoInventoryTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Reforge/DtoInventory.cs test/Reforge.Tests/DtoInventoryTests.cs
git commit -m "feat(surface-score): add recursive path-based DTO inventory"
```

---

## Task 6: Add the Camps read/full + nested Info DTO fixture

Adds the shapes Plans B and C assert against. Kept in the existing `CampFixtures.cs`.

**Files:**
- Modify: `test/SampleSolution/SampleSolution.Services/CampFixtures.cs`

- [ ] **Step 1: Append the fixture types**

Append to `test/SampleSolution/SampleSolution.Services/CampFixtures.cs`:

```csharp
// --- Section-architecture fixtures (read/full pair + nested canonical Info DTO) ---

public interface ICampServiceRead
{
    Task<CampInfo> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<CampSettingsInfo> GetSettingsAsync(Guid campId, CancellationToken ct = default);
    Task<bool> IsUserCampLeadAsync(Guid campId, Guid userId, CancellationToken ct = default);            // predicate (charged)
    Task<List<CampSummary>> GetCampSummariesForYearAsync(int year, CancellationToken ct = default);      // projection (charged)
}

public interface ICampSectionService : ICampServiceRead
{
    Task RenameAsync(Guid id, string name, CancellationToken ct = default);
}

public sealed class CampSectionService : ICampSectionService
{
    public Task<CampInfo> GetByIdAsync(Guid id, CancellationToken ct = default) => Task.FromResult(new CampInfo());
    public Task<CampSettingsInfo> GetSettingsAsync(Guid campId, CancellationToken ct = default) => Task.FromResult(new CampSettingsInfo());
    public Task<bool> IsUserCampLeadAsync(Guid campId, Guid userId, CancellationToken ct = default) => Task.FromResult(false);
    public Task<List<CampSummary>> GetCampSummariesForYearAsync(int year, CancellationToken ct = default) => Task.FromResult(new List<CampSummary>());
    public Task RenameAsync(Guid id, string name, CancellationToken ct = default) => Task.CompletedTask;
}

public sealed class CampInfo
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public List<CampSeasonInfo> Seasons { get; set; } = new();
    public CampSeasonInfo? CurrentSeason { get; set; }
    public List<string> ImageUrls { get; set; } = new();
}

public sealed class CampSeasonInfo
{
    public int Year { get; set; }
    public List<CampMemberInfo> Members { get; set; } = new();
}

public sealed class CampMemberInfo
{
    public Guid UserId { get; set; }
    public bool IsLead { get; set; }
}

public sealed class CampSettingsInfo
{
    public int CurrentYear { get; set; }
    public DateTime NameLockDate { get; set; }
}

public sealed class CampSummary
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
}
```

- [ ] **Step 2: Build to verify the fixture compiles**

Run: `dotnet build Reforge.slnx`
Expected: build succeeds (the sample solution project compiles the new types).

- [ ] **Step 3: Run the full suite (no regressions from new fixtures)**

Run: `dotnet test Reforge.slnx`
Expected: all tests PASS. New public types may shift some existing surface-score *counts*; if a hard-coded count assertion in `SurfaceScoreTests.cs` breaks, update that assertion to the new value (the fixtures are additive and legitimate) and note it in the commit.

- [ ] **Step 4: Commit**

```bash
git add test/SampleSolution/SampleSolution.Services/CampFixtures.cs test/Reforge.Tests/SurfaceScoreTests.cs
git commit -m "test(surface-score): add Camps read/full + nested Info DTO fixtures"
```

---

## Final Verification

- [ ] **Run the full suite**

Run: `dotnet test Reforge.slnx`
Expected: all tests green (original 70 + the new foundation tests).

- [ ] **Confirm no command output changed**

Run: `dotnet run --project src/Reforge -- surface-score --solution test/SampleSolution/SampleSolution.slnx --format json | head -5`
Expected: runs without error; `surfaceTotal`/`internalComplexityTotal` present. (Counts may differ slightly from before Task 6 because of the additive fixtures; no new rules or fields yet.)

---

## Self-Review Notes

- **Spec coverage (Section 1):** config fields (Task 1), repo-backed gating inputs (Task 3), cacheDto field present (Task 1; *inference* logic deferred to Plan B where section-shape consumes it). Read-method classifier (Task 4) and recursive inventory (Task 5) are the shared pieces Sections 3-5 build on.
- **Deferred to Plan B:** `cacheDto` inference from a caching decorator, `SectionShapeAnalyzer`, all five scored rules, advisory candidates, `conservationAnchors` emission, glossary lines.
- **Deferred to Plan C:** the baseline conservation gate (evidence rows, `coverageKind`/`targetDto`, helper detection + ordering, the three verdict kinds).
- **Type consistency:** `SolutionClassifier.ClassifyAsync` returns `IReadOnlyList<ClassifiedType>`; engine call sites `.ToList()` it. `ReadSurface.Classify(IMethodSymbol, string?, string?)` and `DtoInventory.Build(INamedTypeSymbol, IReadOnlySet<string>, int)` signatures are fixed here and consumed unchanged in later plans.
- **Risk:** Task 2 is a behavior-preserving extraction guarded by the existing 70 tests. If any existing surface-score test changes value, the move was not behavior-preserving - fix before proceeding.
