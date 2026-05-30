# Degraded-Build Detection Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make `surface-score` detect and loudly surface when it analyzed a solution that did not compile cleanly, so a partial score is never mistaken for a complete one.

**Architecture:** A new pure analyzer (`BuildInspector`) counts compile errors / unresolved references across the solution's Roslyn compilations and runs a best-effort filesystem "appears unbuilt" probe. `SurfaceScoreEngine.ScoreAsync` calls it once (reusing already-realized, Roslyn-cached compilations) and stores a `BuildHealth` record on `ScoreReport`. `SurfaceScoreCommand` emits an additive `build` JSON object, pushes a `degraded-build` diagnostic into the existing diagnostics array (so compact/markdown render it too), and writes a `WARNING:` line to stderr. Diagnostic-only: no score math changes.

**Tech Stack:** .NET 10, Roslyn (Microsoft.CodeAnalysis), System.CommandLine v3, xUnit.

**Spec:** `docs/superpowers/specs/2026-05-30-degraded-build-detection-design.md`

---

## Design decisions locked in (read before starting)

- **`BuildHealth` is a record stored on `ScoreReport` as one property**, not four flat fields. The spec lists four conceptual fields (`BuildDegraded`, `CompilationErrorCount`, `UnresolvedReferenceCount`, `AppearsUnbuilt`); they live as the four members of the `BuildHealth` record. The JSON `build` object maps to those four members. This is functionally identical to four flat fields and is more testable (the analyzer returns the record directly).
- **`ScoreReport.BuildHealth` defaults to a non-degraded value** (`new(false, 0, 0, false)`) so the `build` object is always emitted, even on the (impossible-in-practice) path where the analyzer is never called.
- **The engine sets data only.** The `degraded-build` diagnostic string and the stderr `WARNING:` line are built in the command (presentation), using a pure message builder `BuildInspector.DescribeDegraded` so the wording is unit-tested without driving Console output.
- **Unresolved-reference codes:** `CS0246` (type/namespace not found), `CS0234` (name not in namespace), `CS0012` (type in unreferenced assembly).
- **"Appears unbuilt" probe:** a project "looks built" if its sibling `obj/` directory contains any `*.cs` file recursively (build generates `GlobalUsings.g.cs`, `*.AssemblyInfo.cs`, etc.; restore alone does not). `appearsUnbuilt` is true only when at least one project path is inspectable AND none look built. Unknown/unreadable paths contribute nothing (never a false alarm). All-unknown -> false.
- **CHANGELOG.md does not exist yet.** CLAUDE.md says to maintain one but it was never created. Task 4 creates it fresh with a header + the v0.18.1 entry (newest-first convention for future entries).

## File structure

- **Create** `src/Reforge/BuildInspector.cs` - the `BuildHealth` record + `BuildInspector` static analyzer (error counting, unbuilt probe, message builder). One responsibility: assess compilation health. ~70 lines.
- **Create** `test/Reforge.Tests/BuildInspectorTests.cs` - unit tests for the analyzer using in-memory `CSharpCompilation`s and temp dirs.
- **Modify** `src/Reforge/SurfaceScoreEngine.cs` - add `BuildHealth` property to `ScoreReport` (near line 43-45); call `BuildInspector.InspectAsync` at the end of `ScoreAsync` (after line 129, before `return report;`).
- **Modify** `test/Reforge.Tests/SurfaceScoreTests.cs` - add one integration test: the built sample solution scores `Degraded == false`.
- **Modify** `src/Reforge/Commands/SurfaceScoreCommand.cs` - add the `degraded-build` diagnostic + stderr warning in `SetAction` (after line 95); add the `build` object to the JSON payload in `WriteJson` (around line 557).
- **Modify** `src/Reforge/Reforge.csproj` - bump `<Version>` 0.17.0 -> 0.18.1.
- **Create** `CHANGELOG.md` - new file (header + v0.18.1 entry).

---

## Task 1: BuildInspector analyzer (pure, unit-tested)

**Files:**
- Create: `src/Reforge/BuildInspector.cs`
- Test: `test/Reforge.Tests/BuildInspectorTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `test/Reforge.Tests/BuildInspectorTests.cs`:

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Reforge.Tests;

public class BuildInspectorTests
{
    private static Compilation Compile(string source) =>
        CSharpCompilation.Create("t",
            new[] { CSharpSyntaxTree.ParseText(source) },
            new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) });

    [Fact]
    public void CountErrors_CleanSource_IsZero()
    {
        var comp = Compile("public sealed class Ok { public int X { get; set; } }");
        var (errors, unresolved) = BuildInspector.CountErrors(new[] { comp }, CancellationToken.None);
        Assert.Equal(0, errors);
        Assert.Equal(0, unresolved);
    }

    [Fact]
    public void CountErrors_UnresolvedBaseType_CountsErrorAndUnresolved()
    {
        // `Undefined` is not declared -> CS0246 (type or namespace not found).
        var comp = Compile("public sealed class Broken : Undefined { }");
        var (errors, unresolved) = BuildInspector.CountErrors(new[] { comp }, CancellationToken.None);
        Assert.True(errors >= 1, $"expected >=1 error, got {errors}");
        Assert.True(unresolved >= 1, $"expected >=1 unresolved, got {unresolved}");
    }

    [Fact]
    public void AppearsUnbuilt_ProjectWithObjCsArtifacts_IsFalse()
    {
        var dir = Path.Combine(Path.GetTempPath(), "reforge-bi-built-" + Guid.NewGuid().ToString("N"));
        var objDir = Path.Combine(dir, "obj", "Debug", "net10.0");
        Directory.CreateDirectory(objDir);
        File.WriteAllText(Path.Combine(objDir, "Proj.GlobalUsings.g.cs"), "// generated");
        var projPath = Path.Combine(dir, "Proj.csproj");
        File.WriteAllText(projPath, "<Project/>");
        try
        {
            Assert.False(BuildInspector.AppearsUnbuilt(new[] { (string?)projPath }));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void AppearsUnbuilt_ProjectWithoutObjArtifacts_IsTrue()
    {
        var dir = Path.Combine(Path.GetTempPath(), "reforge-bi-unbuilt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var projPath = Path.Combine(dir, "Proj.csproj");
        File.WriteAllText(projPath, "<Project/>");
        try
        {
            Assert.True(BuildInspector.AppearsUnbuilt(new[] { (string?)projPath }));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void AppearsUnbuilt_NoKnownPaths_IsFalse()
    {
        Assert.False(BuildInspector.AppearsUnbuilt(new string?[] { null, "" }));
    }

    [Fact]
    public void DescribeDegraded_UnbuiltWording_MentionsDotnetBuild()
    {
        var h = new BuildHealth(Degraded: true, CompilationErrorCount: 142, UnresolvedReferenceCount: 37, AppearsUnbuilt: true);
        var msg = BuildInspector.DescribeDegraded(h);
        Assert.Contains("unbuilt", msg, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("dotnet build", msg, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("142", msg);
    }

    [Fact]
    public void DescribeDegraded_ErrorsButBuilt_MentionsCompileErrors()
    {
        var h = new BuildHealth(Degraded: true, CompilationErrorCount: 3, UnresolvedReferenceCount: 0, AppearsUnbuilt: false);
        var msg = BuildInspector.DescribeDegraded(h);
        Assert.Contains("3", msg);
        Assert.Contains("error", msg, StringComparison.OrdinalIgnoreCase);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Reforge.slnx --filter FullyQualifiedName~BuildInspectorTests`
Expected: FAIL to compile - `BuildInspector` and `BuildHealth` do not exist yet.

- [ ] **Step 3: Write the implementation**

Create `src/Reforge/BuildInspector.cs`:

```csharp
using Microsoft.CodeAnalysis;

namespace Reforge;

/// <summary>
/// Build-health of an analyzed solution. When <see cref="Degraded"/> is true the
/// semantic model is incomplete (the solution did not compile cleanly), so any
/// score computed against it is partial. <see cref="AppearsUnbuilt"/> only flavors
/// the warning wording; it is not the authoritative degraded signal.
/// </summary>
public sealed record BuildHealth(
    bool Degraded,
    int CompilationErrorCount,
    int UnresolvedReferenceCount,
    bool AppearsUnbuilt);

/// <summary>
/// Assesses whether a solution compiled cleanly. surface-score relies on a complete
/// semantic model; an unbuilt/erroring solution silently under-counts cross-project
/// rules (DI registration, cross-section service/interface, entity-return). This
/// inspector surfaces that state. Counts only - no diagnostic messages are retained.
/// </summary>
public static class BuildInspector
{
    // Canonical "didn't build / didn't restore" diagnostic codes:
    // CS0246 type-or-namespace not found, CS0234 name not in namespace,
    // CS0012 type defined in an unreferenced assembly.
    private static readonly HashSet<string> UnresolvedReferenceCodes =
        new(StringComparer.Ordinal) { "CS0246", "CS0234", "CS0012" };

    /// <summary>
    /// Inspects every project's compilation plus the on-disk build-artifact probe.
    /// Reuses Roslyn's per-project compilation cache, so calling this after the
    /// scoring passes adds no meaningful compilation cost.
    /// </summary>
    public static async Task<BuildHealth> InspectAsync(Solution solution, CancellationToken ct)
    {
        var compilations = new List<Compilation>();
        foreach (var project in solution.Projects)
        {
            var compilation = await project.GetCompilationAsync(ct);
            if (compilation is not null) compilations.Add(compilation);
        }

        var (errors, unresolved) = CountErrors(compilations, ct);
        var appearsUnbuilt = AppearsUnbuilt(solution.Projects.Select(p => p.FilePath));
        return new BuildHealth(errors > 0, errors, unresolved, appearsUnbuilt);
    }

    /// <summary>Counts error-severity diagnostics across the given compilations, and the unresolved-reference subset.</summary>
    internal static (int errors, int unresolved) CountErrors(IEnumerable<Compilation> compilations, CancellationToken ct)
    {
        int errors = 0, unresolved = 0;
        foreach (var compilation in compilations)
        {
            foreach (var d in compilation.GetDiagnostics(ct))
            {
                if (d.Severity != DiagnosticSeverity.Error) continue;
                errors++;
                if (UnresolvedReferenceCodes.Contains(d.Id)) unresolved++;
            }
        }
        return (errors, unresolved);
    }

    /// <summary>
    /// Best-effort: true when at least one project path is inspectable and none show
    /// build artifacts (any <c>*.cs</c> under a sibling <c>obj/</c>). Unknown/unreadable
    /// paths contribute nothing. All-unknown returns false (can't tell).
    /// </summary>
    internal static bool AppearsUnbuilt(IEnumerable<string?> projectFilePaths)
    {
        bool anyInspectable = false;
        foreach (var path in projectFilePaths)
        {
            if (string.IsNullOrEmpty(path)) continue;
            var dir = Path.GetDirectoryName(path);
            if (dir is null) continue;
            var objDir = Path.Combine(dir, "obj");

            bool looksBuilt;
            try
            {
                looksBuilt = Directory.Exists(objDir)
                    && Directory.EnumerateFiles(objDir, "*.cs", SearchOption.AllDirectories).Any();
            }
            catch
            {
                continue; // unreadable -> unknown, skip
            }

            anyInspectable = true;
            if (looksBuilt) return false; // at least one project built -> not "unbuilt"
        }
        return anyInspectable; // saw projects, none built -> appears unbuilt
    }

    /// <summary>Human/agent-facing one-line description of a degraded build. Pure; no I/O.</summary>
    public static string DescribeDegraded(BuildHealth h)
    {
        var counts = $"{h.CompilationErrorCount} compile error(s), {h.UnresolvedReferenceCount} unresolved reference(s)";
        return h.AppearsUnbuilt
            ? $"Solution appears unbuilt ({counts}). Surface-score is PARTIAL: cross-section/DI/entity rules under-count. Run `dotnet build` first, then re-run."
            : $"Solution did not compile cleanly ({counts}). Surface-score is PARTIAL: cross-section/DI/entity rules may under-count.";
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test Reforge.slnx --filter FullyQualifiedName~BuildInspectorTests`
Expected: PASS (7 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Reforge/BuildInspector.cs test/Reforge.Tests/BuildInspectorTests.cs
git commit -m "feat(surface-score): add BuildInspector compilation-health analyzer

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Task 2: Wire BuildHealth into ScoreReport and the engine

**Files:**
- Modify: `src/Reforge/SurfaceScoreEngine.cs` (ScoreReport ~line 43-45; ScoreAsync ~line 129-131)
- Test: `test/Reforge.Tests/SurfaceScoreTests.cs`

- [ ] **Step 1: Write the failing integration test**

Append this test to `test/Reforge.Tests/SurfaceScoreTests.cs` (inside the `SurfaceScoreTests` class, before its closing brace). It uses the existing `_fixture` which opens the built sample solution:

```csharp
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
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test Reforge.slnx --filter FullyQualifiedName~ScoreAsync_BuiltSampleSolution_IsNotDegraded`
Expected: FAIL to compile - `ScoreReport.BuildHealth` does not exist yet.

- [ ] **Step 3: Add the BuildHealth property to ScoreReport**

In `src/Reforge/SurfaceScoreEngine.cs`, in the `ScoreReport` class, add this property right after `public int TypesAnalyzed { get; set; }` (currently line 45):

```csharp
    /// <summary>
    /// Compilation health of the analyzed solution. Defaults to a non-degraded value
    /// so the JSON `build` object is always present. Populated by <see cref="SurfaceScoreEngine.ScoreAsync"/>.
    /// </summary>
    public BuildHealth BuildHealth { get; set; } = new(false, 0, 0, false);
```

- [ ] **Step 4: Call the inspector at the end of ScoreAsync**

In `src/Reforge/SurfaceScoreEngine.cs`, in `ScoreAsync`, immediately before `return report;` (currently line 131), add:

```csharp
        // Build health: detect a degraded (unbuilt/erroring) compilation so a partial
        // score is never mistaken for a complete one. Reuses the per-project compilations
        // the passes above already realized (Roslyn caches them), so this is near-free.
        report.BuildHealth = await BuildInspector.InspectAsync(solution, ct);

```

- [ ] **Step 5: Run the test to verify it passes**

Run: `dotnet test Reforge.slnx --filter FullyQualifiedName~ScoreAsync_BuiltSampleSolution_IsNotDegraded`
Expected: PASS.

- [ ] **Step 6: Run the full suite to confirm no regressions**

Run: `dotnet test Reforge.slnx`
Expected: PASS - all prior tests (88) + Task 1 (7) + this (1) green.

- [ ] **Step 7: Commit**

```bash
git add src/Reforge/SurfaceScoreEngine.cs test/Reforge.Tests/SurfaceScoreTests.cs
git commit -m "feat(surface-score): compute BuildHealth during ScoreAsync

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Task 3: Surface degraded build in command output (JSON, diagnostics, stderr)

**Files:**
- Modify: `src/Reforge/Commands/SurfaceScoreCommand.cs` (SetAction ~after line 95; WriteJson payload ~line 557)

- [ ] **Step 1: Add the degraded-build diagnostic and stderr warning in SetAction**

In `src/Reforge/Commands/SurfaceScoreCommand.cs`, in `SetAction`, immediately after `report.ConfigPath = loadedFrom;` (currently line 95), add:

```csharp

                // Build-health: when the analyzed solution did not compile cleanly, the
                // score is partial. Surface it in every format (diagnostics array -> compact
                // & markdown render it) and shout to stderr (never stdout, which may be JSON).
                if (report.BuildHealth.Degraded)
                {
                    var buildMsg = BuildInspector.DescribeDegraded(report.BuildHealth);
                    report.Diagnostics.Add(new ScoreDiagnostic("warning", "degraded-build", buildMsg));
                    Console.Error.WriteLine($"WARNING: {buildMsg}");
                }
```

- [ ] **Step 2: Add the `build` object to the JSON payload**

In `src/Reforge/Commands/SurfaceScoreCommand.cs`, in `WriteJson`, in the `payload` anonymous object, add a `build` member right after the `typesAnalyzed = report.TypesAnalyzed,` line (currently line 557):

```csharp
            build = new
            {
                degraded = report.BuildHealth.Degraded,
                compilationErrorCount = report.BuildHealth.CompilationErrorCount,
                unresolvedReferenceCount = report.BuildHealth.UnresolvedReferenceCount,
                appearsUnbuilt = report.BuildHealth.AppearsUnbuilt
            },
```

- [ ] **Step 3: Build to verify it compiles**

Run: `dotnet build Reforge.slnx`
Expected: Build succeeded (CRLF/NU warnings are normal).

- [ ] **Step 4: Dogfood - built solution shows degraded:false**

Run:
```bash
dotnet run --project src/Reforge -- surface-score --solution test/SampleSolution/SampleSolution.slnx --format json | tail -n +1 > /tmp/ss-built.json
cat /tmp/ss-built.json | grep -A6 '"build"'
```
Expected: a `"build"` object with `"degraded": false`, `"compilationErrorCount": 0`. No `WARNING:` on stderr.

- [ ] **Step 5: Dogfood - unbuilt solution shows degraded:true + stderr warning**

Remove build artifacts from the sample solution, then re-run and capture stderr separately:
```bash
find test/SampleSolution -type d \( -name obj -o -name bin \) -prune -exec rm -rf {} +
dotnet run --project src/Reforge -- surface-score --solution test/SampleSolution/SampleSolution.slnx --format json 1>/tmp/ss-unbuilt.json 2>/tmp/ss-unbuilt.err
echo "--- build object ---"; grep -A6 '"build"' /tmp/ss-unbuilt.json
echo "--- stderr ---"; grep WARNING /tmp/ss-unbuilt.err
echo "--- stdout still valid json? ---"; cat /tmp/ss-unbuilt.json | python -c "import sys,json; json.load(sys.stdin); print('valid json')"
```
Expected: `"degraded": true` with `compilationErrorCount >= 1`; a `WARNING:` line on stderr; stdout parses as valid JSON.

NOTE: `dotnet run` will rebuild Reforge itself (its own obj/bin are untouched - only `test/SampleSolution/**` artifacts were removed). The sample solution is opened by MSBuildWorkspace, not built, so it stays unbuilt for the query.

- [ ] **Step 6: Restore the sample solution build state**

Run: `dotnet build test/SampleSolution/SampleSolution.slnx`
Expected: Build succeeded. (Confirms the unbuilt state was the cause; leaves the tree ready for the test suite.)

- [ ] **Step 7: Confirm previously-hidden rules reappear when built**

Run:
```bash
dotnet run --project src/Reforge -- surface-score --solution test/SampleSolution/SampleSolution.slnx --format json 1>/tmp/ss-rebuilt.json 2>/dev/null
echo "built diRegistration:"; grep -o '"diRegistration": *[0-9]*' /tmp/ss-rebuilt.json | head -1
echo "unbuilt diRegistration:"; grep -o '"diRegistration": *[0-9]*' /tmp/ss-unbuilt.json | head -1
```
Expected: the built run shows a `diRegistration` total >= the unbuilt run (the cross-project rule recovers once references resolve). If the sample solution is too small to exercise it, note that and rely on the Humans dogfood in Task 5.

- [ ] **Step 8: Run the full suite**

Run: `dotnet test Reforge.slnx`
Expected: PASS - all tests green (96 total).

- [ ] **Step 9: Commit**

```bash
git add src/Reforge/Commands/SurfaceScoreCommand.cs
git commit -m "feat(surface-score): emit build-health (json build object, diagnostic, stderr warning)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Task 4: Version bump + CHANGELOG

**Files:**
- Modify: `src/Reforge/Reforge.csproj:11`
- Create: `CHANGELOG.md`

- [ ] **Step 1: Bump the version**

In `src/Reforge/Reforge.csproj`, change line 11 from:

```xml
    <Version>0.17.0</Version>
```
to:
```xml
    <Version>0.18.1</Version>
```

- [ ] **Step 2: Create CHANGELOG.md**

`CHANGELOG.md` does not exist yet. Create it at the repo root with a header followed by the v0.18.1 entry (newest entries go on top in future releases):

```markdown
# Changelog

What changed and why. Newest first.

## v0.18.1 - degraded-build detection (issue #9)

surface-score now detects when it analyzed a solution that did not compile cleanly
and would otherwise silently under-count cross-project rules (diRegistration,
crossSectionFullService, crossSectionReadInterface, methodReturnsEntityAcrossSection).
Why: a partial score read as complete corrupts baseline comparisons - and Plan C's
conservation gate compares two scores per commit, so this had to land first.

- New `BuildInspector` counts error-severity diagnostics + the unresolved-reference
  subset (CS0246/CS0234/CS0012) across all project compilations, plus a best-effort
  "appears unbuilt" filesystem probe (no `*.cs` under any project `obj/`).
- New additive `build` object in `--format json`:
  `{ degraded, compilationErrorCount, unresolvedReferenceCount, appearsUnbuilt }`.
  The existing `diagnostics` array is unchanged.
- A `degraded-build` warning is pushed into the diagnostics array (compact + markdown
  render it) and a prominent `WARNING:` line is written to stderr (never stdout).
- Diagnostic-only: no score math changed. Exit code stays 0; gating is deferred to Plan C.

```

- [ ] **Step 3: Build + full suite (sanity after version change)**

Run: `dotnet build Reforge.slnx && dotnet test Reforge.slnx`
Expected: Build succeeded; all tests green.

- [ ] **Step 4: Commit (feature files) - version bump committed separately**

Per the user's shipping flow, the version bump is its own commit, separate from feature files. The CHANGELOG goes with the feature work; the csproj goes alone.

```bash
git add CHANGELOG.md
git commit -m "docs: changelog for v0.18.1 degraded-build detection

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
git add src/Reforge/Reforge.csproj
git commit -m "Bump to v0.18.1

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Task 5 (verification, not code): Humans dogfood

**Not a code task - a verification gate before declaring done.** Run only after Tasks 1-4 are committed.

- [ ] **Step 1: Score Humans unbuilt vs built, compare degraded flag**

```bash
# (a) Without building Humans first - expect degraded:true if its artifacts are stale/absent
timeout 420 dotnet run --project src/Reforge -- surface-score --solution /h/source/humans/Humans.slnx --format json 1>/tmp/humans-ss.json 2>/tmp/humans-ss.err
echo "--- build object ---"; grep -A6 '"build"' /tmp/humans-ss.json
echo "--- stderr warning (if any) ---"; grep WARNING /tmp/humans-ss.err
```
Expected: the `build` object is present. If Humans is currently built, `degraded:false`; if not, `degraded:true` with a stderr WARNING. Either is a correct result - the point is the signal is now present and honest.

- [ ] **Step 2: Confirm the historical gap is now visible**

If `degraded:true`, this is exactly the issue-#9 scenario now correctly surfaced. Record the `compilationErrorCount` / `unresolvedReferenceCount` in the final report to the user. No further code action.

---

## Self-review (completed during planning)

**Spec coverage:**
- Detection (Opt 1: error + unresolved counts) -> Task 1 `CountErrors`.
- Unbuilt heuristic (Opt 2, message-only) -> Task 1 `AppearsUnbuilt` + `DescribeDegraded`.
- Placement in surface-score path only -> Task 2 (engine), no WorkspaceHelper change.
- Additive `build` JSON object, existing `diagnostics` array untouched -> Task 3 Step 2.
- `degraded-build` diagnostic in array (compact/markdown) -> Task 3 Step 1.
- stderr WARNING, exit 0 -> Task 3 Step 1.
- `build` always emitted (default non-degraded) -> Task 2 Step 3 default + record.
- Version bump 0.18.1 -> Task 4.
- Acceptance #1-#7 -> Task 3 dogfood Steps 4-7, Task 1 unit tests, Task 5 Humans dogfood.

**Placeholder scan:** none. Every code step shows complete code.

**Type consistency:** `BuildHealth(Degraded, CompilationErrorCount, UnresolvedReferenceCount, AppearsUnbuilt)` used identically in the record def (Task 1), the `ScoreReport` default (Task 2), the JSON writer (Task 3), and tests. `BuildInspector.InspectAsync` / `CountErrors` / `AppearsUnbuilt` / `DescribeDegraded` signatures match across tasks. `LocationHelper.GetSolutionDirectory` (used in Task 2 test) is the same call the command uses at `SurfaceScoreCommand.cs:90`.
