# Degraded-Build Detection for surface-score - Design Spec

## Status

Approved (2026-05-30). Targets reforge v0.18.1 (patch). Fixes issue #9.

## Context

`surface-score` opens a solution through MSBuildWorkspace and computes a semantic
model. When the solution has not been built/restored, MSBuildWorkspace still opens
it successfully, but the per-project `Compilation` is full of unresolved-reference
errors (`CS0246` type-or-namespace not found, `CS0234` name does not exist in
namespace, `CS0012` type defined in an unreferenced assembly).

The scoring passes that rely on cross-project semantics silently under-count in
this state. Observed in issue #9: the same commit scored 57177 unbuilt vs 59377
built, with identical `typesAnalyzed` and an empty `diagnostics` array. The hidden
gap concentrates in exactly the rules a baseline comparison cares about:
`diRegistration`, `crossSectionFullService`, `crossSectionReadInterface`,
`methodReturnsEntityAcrossSection`.

The only build signal today is `WorkspaceHelper`'s `RegisterWorkspaceFailedHandler`,
which forwards workspace load failures to stderr. It does not fire for an
openable-but-unbuilt solution, so nothing tells the caller the result is partial.

This matters now because Plan C's baseline conservation gate will compare two
scores per commit. If either score was produced against an unbuilt solution, the
gate inherits the under-count as a false signal. Detecting and surfacing the
degraded state is a prerequisite.

## Goal

Detect when the analyzed solution did not compile cleanly, and surface that fact
prominently, so a caller (human or agent) never mistakes a partial score for a
complete one. Counts in machine-readable output; a loud warning to stderr.

## Non-goals

- Not refusing to score, and not adding `--allow-degraded`. Exit code stays 0.
  Gating on degraded state is deferred to Plan C (per the issue).
- Not changing any score math. No rule weights change; no rule is added or removed.
  This is a diagnostic-only release.
- Not storing individual diagnostic messages. A broken solution can emit thousands;
  we store counts, not text.
- Not touching `WorkspaceHelper` or the lightweight query commands (`references`,
  `callers`, etc.). They must stay fast. Detection lives only in the surface-score
  path.

## Design

### Placement

Detection runs inside `SurfaceScoreEngine.ScoreAsync(Solution, CancellationToken)`.
That method already traverses `solution.Projects` and calls
`project.GetCompilationAsync(ct)` in several passes; Roslyn caches the realized
`Compilation` per project, so adding a diagnostics scan reuses already-built
compilations rather than paying for them twice. Results are written onto
`ScoreReport`. `SurfaceScoreCommand` remains a thin writer.

### 1. Build-health computation (Option 1, the substance)

A new private step in `ScoreAsync` iterates `solution.Projects`, awaits each
`GetCompilationAsync(ct)`, and scans `compilation.GetDiagnostics(ct)`:

- `compilationErrorCount` = total diagnostics with `Severity == DiagnosticSeverity.Error`,
  summed across all projects.
- `unresolvedReferenceCount` = the subset of those errors whose `Id` is one of
  `CS0246`, `CS0234`, `CS0012` (the canonical "not built / not restored" codes).
- `buildDegraded = compilationErrorCount > 0`.

Keyed off any compile error, not only unresolved references: real code errors also
degrade the semantic model, and a caller should know the score is partial either way.
The `unresolvedReferenceCount` breakout exists so the warning can distinguish
"looks unbuilt" from "your code has errors".

Counts only. No diagnostic messages are retained.

### 2. Unbuilt heuristic (Option 2, message flavoring only)

Best-effort filesystem probe to make the warning friendlier. For each project with
a non-null `FilePath`, check whether its sibling `obj/` directory contains build
artifacts (any `*.g.cs` generated file, e.g. `*.GlobalUsings.g.cs` or
`*.AssemblyInfo.cs`). If no project in the solution shows any artifacts,
`appearsUnbuilt = true`.

This does NOT flip `buildDegraded` (Option 1 owns the authoritative signal); it
only selects the warning wording: "solution appears unbuilt - run `dotnet build`
first" vs a bare error-count message. If a project's `FilePath` is null or its
`obj/` is unreadable, that project contributes nothing to the probe (treated as
unknown, never a false alarm). If every project is unknown, `appearsUnbuilt`
stays `false`.

### 3. Output (all additive, non-breaking)

New fields on `ScoreReport`:

- `bool BuildDegraded`
- `int CompilationErrorCount`
- `int UnresolvedReferenceCount`
- `bool AppearsUnbuilt`

JSON: a new top-level object. The existing `diagnostics` array is unchanged.

```json
"build": {
  "degraded": true,
  "compilationErrorCount": 142,
  "unresolvedReferenceCount": 37,
  "appearsUnbuilt": true
}
```

The `build` object is always emitted (degraded or not) so consumers can rely on
its presence. When not degraded: `{ "degraded": false, "compilationErrorCount": 0,
"unresolvedReferenceCount": 0, "appearsUnbuilt": false }`.

Diagnostics array + compact + markdown: when `buildDegraded`, push one
`ScoreDiagnostic("warning", "degraded-build", <message>)` so all three formats
surface the same signal through the existing diagnostics rendering. The message
incorporates `appearsUnbuilt` wording and the two counts.

stderr: when `buildDegraded`, write a prominent `WARNING: ...` line to
`Console.Error`, regardless of `--format`. Per project convention, build signal
goes to stderr and never pollutes stdout (which may be parsed as JSON).

Exit code stays 0.

### Version

Bump `<Version>` in `src/Reforge/Reforge.csproj` from `0.17.0` to `0.18.1`
(patch: diagnostic only, no score-math change).

## Acceptance criteria

1. Running `surface-score --format json` against a solution with one or more
   compile errors emits `"build": { "degraded": true, ... }` with
   `compilationErrorCount >= 1`, and a `degraded-build` entry in the `diagnostics`
   array.
2. The same run prints a `WARNING:` line to stderr; stdout remains valid JSON
   (the warning is not on stdout).
3. Running against a cleanly-built solution emits `"build": { "degraded": false,
   "compilationErrorCount": 0, "unresolvedReferenceCount": 0, "appearsUnbuilt":
   false }`, no `degraded-build` diagnostic, and no stderr warning.
4. A solution with unresolved references (e.g. a type from an unreferenced project)
   reports `unresolvedReferenceCount >= 1`.
5. The `appearsUnbuilt` heuristic returns true when no project has `obj/` artifacts
   and false when at least one does (unit-tested directly).
6. No existing score value changes for a built solution: existing surface-score
   tests still pass unchanged (88/88 baseline preserved), plus the new tests.
7. Dogfood: against `test/SampleSolution` built, `degraded == false`; after
   removing `obj/` and `bin/`, `degraded == true` and the previously-hidden
   cross-section / DI / entity rules reappear once rebuilt.

## Notes

- The `diagnostics` field in issue #9 ("diagnostics: 0") referred to the existing
  array being empty, not a count field. The build signal is therefore a new
  additive field, not a repurpose of `diagnostics`.
- Test harness for the unit tests builds in-memory `CSharpCompilation`s with a
  minimal reference set (corelib, Linq, Collections, DataAnnotations) so a "clean"
  fixture has zero errors and a "broken" fixture (e.g. `class C : Undefined {}`)
  produces a CS0246.
