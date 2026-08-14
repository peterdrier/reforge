# Reforge — as-is inventory

Audited 2026-08-14 against `fe332d9` (v0.25.0). Read-only; no code changed.

## Shape

| | |
|---|---|
| Production C# | 13,379 lines / 54 files, one project (`src/Reforge`) |
| Tests | 3,745 lines / 28 files, 201 tests, all green |
| Test fixture | `test/SampleSolution/` — 8 projects, 1,580 LOC, no PackageReferences |
| Commits | 101, since 2026-04-11 |
| Build/CI | `dotnet build` clean, 0 warnings. **No CI, no `.editorconfig`, no `Directory.Build.props`, no analyzers.** |

Single assembly, flat namespace `Reforge`, commands in `Reforge.Commands`. Each command is a
static class with one `Create(solutionOption, formatOption, limitOption)` returning a
`System.CommandLine.Command`. That shape is sound and should stay.

## External surface

### 28 CLI commands

Usage counts are from `~/.reforge/usage.log` — **4,516 real invocations** since 2026-04-11,
which makes this repo unusually well-instrumented for a surface audit.

| Command | All-time | Last 90d | Notes |
|---|---:|---:|---|
| `audit-surface` | 891 | 857 | inbound per-method view |
| `audit-downstream` | 834 | 824 | outbound per-method view |
| `snapshot` | 666 | 51 | **broken under hot mode** (S001) |
| `surface-score` | 665 | 665 | **broken under hot mode** (S001) |
| `callers` | 383 | 285 | |
| `references` | 285 | 121 | |
| `injected` | 253 | 154 | |
| `dbset-usage` | 172 | 28 | |
| `members` | 161 | 54 | |
| `dependencies` | 52 | 25 | |
| `ownership-violations` | 47 | 35 | |
| `implementations` | 28 | 9 | strict subset of `inheritors` |
| `usages` | 22 | 16 | |
| `health` | 20 | 10 | |
| `service-map` | 14 | 6 | |
| `section-shape` | 6 | 6 | new (2026-05-30); **broken under hot mode** |
| `cycles` | 5 | 0 | **broken under hot mode** |
| `audit-ef` | 3 | 0 | |
| `audit-cache` | 3 | 0 | |
| `call-chain` | 2 | 1 | |
| `audit-immutable` | 2 | 0 | |
| `audit-auth` | 2 | 0 | |
| `inheritors` | **0** | 0 | never invoked |
| `parameters` | **0** | 0 | never invoked |
| `serve` / `stop` / `skill` / `install` / `request` | n/a | n/a | not telemetered |

Five commands account for 3,439 of 4,516 runs (76%). Eight are at or near zero.

**Surface judgment, 2026-08-14: the user reviewed the eight zero/near-zero commands and chose to
keep all of them.** No `cut` items in the backlog. Recorded here so a re-audit does not re-litigate
it — the telemetry will keep saying "dead" and the answer is still "keep". Consequence worth naming:
because nothing is being cut, the four-way command-list drift (S001) is *more* load-bearing, not
less, since every kept command has to stay registered in four places.

### Global options

`--solution`, `--format Compact|Json`, `--limit` — all `Recursive = true`, so they attach to every
command. `--limit` is **accepted and silently ignored** by `surface-score`, `section-shape`,
`snapshot`, and `cycles` (ReSharper: `UnusedParameter.Global` on all four). See S005.

### Files written outside the repo

- `~/.reforge/usage.log` — one line per command run. Append-only, never rotated; 434 KB today.
- `~/.reforge/requests.log` — `reforge request "..."` entries. Six so far; four became features.
- `<solutionDir>/.reforge-port` — hot-server port file, deleted on clean shutdown.
- `reforge install` writes a Claude Code skill file globally.

### Config

`reforge.surface-score.json`, searched upward from the solution. Optional; policy only.

## Load-bearing weirdness

Things that look accidental and are not. Do not "simplify" these without reading the reason.

1. **`Program.cs` relays to the hot server before `MSBuildLocator.RegisterDefaults()`.**
   `ServerClient` is deliberately pure TCP with no Roslyn reference so the relay path never pays
   JIT/assembly-load cost. The `RunAsync` split exists so MSBuild registration completes before any
   Roslyn type is JIT'd. Both are load-bearing; the comments say so and they are right.

2. **The hot server processes clients sequentially.** Roslyn workspaces are not thread-safe for
   mutation. Do not "improve" this into a concurrent accept loop.

3. **Workspace reload swaps `HotSolution` and lets the old workspace be GC'd rather than disposing
   it.** In-flight queries may still hold the old `Solution`. Intentional.

4. **`ImplementationComplexity.WriteCallNames` is deliberately narrower than the audit commands'
   write lists** — it excludes `Add`/`Remove` because `List.Add` in a query body would strip the
   read exemption. The comment at `ImplementationComplexity.cs:205` explains it. When S002 unifies
   the write-API heuristics, this one must stay a distinct, documented policy, not get merged flat.

5. **Test `SampleSolution` has no `PackageReference` anywhere.** This is what keeps it from
   degrading MSBuildWorkspace loads. Adding a package reference to the fixture will break
   build-health tests in ways that look unrelated.

6. **Dormant `surface-score` config knobs — reviewed 2026-08-14, keep decision.**
   `readShards` / `ReadShard`, `cacheDto`, `settingsInfoDto`, `requiresWriteSurface`,
   `requiresPrimaryInfoDto`, and the whole `weights` map have no real-world consumer: the only
   production config (`H:/source/Humans/reforge.surface-score.json`, 826 lines) sets none of them
   and leaves `weights` empty. ReSharper confirms `ReadShard` is never instantiated and
   `SurfaceScoreBaseline.CachePaths` / `ShardMethods` are populated but never read. The user
   reviewed this and chose to keep all of it. `weights` in particular is the documented way to
   disable a rule (weight `0`), so it is a real escape hatch even unused. **Not backlog material.**

7. **`InternalsVisibleTo(Reforge.Tests)`.** Several `internal` members exist only for tests
   (`SurfaceScoreEngine.BuildFullToReadPairs`, `AuditDownstreamCommand.CollectInstanceMembers`).
   ReSharper's `.Global` dead-code hits on such members are false positives — check the test project
   before deleting anything flagged `.Global`.

## Accidental complexity

### The command surface is declared in four places, and they disagree

| Source | Commands listed | Missing |
|---|---:|---|
| `Program.cs:48-86` | 28 | — (canonical) |
| `ServeCommand.cs:263-283` | 21 | `snapshot`, `cycles`, `surface-score`, `section-shape` |
| `SkillCommand.cs` (agent-facing doc) | 24 | `snapshot`, `cycles`, `section-shape` |
| `README.md` | 14 | 10 service-ownership/health/audit commands |

The `ServeCommand` copy was last updated by `54ab1ec`. Every command added after that date
(`snapshot`+`cycles` 2026-04-15, `surface-score` 2026-05-27, `section-shape` 2026-05-30) was
registered in `Program.cs` and nowhere else. This is omission, not design — the list rots by
construction. **Verified live consequence** (S001): with a server running, `reforge snapshot` and
`reforge surface-score` print the root help text and **exit 0**. `ServerClient.TryRelayAsync`
returns `true` for any completed socket round-trip, and the server redirects stderr to
`TextWriter.Null`, so the failure is silent and looks like success to a scripting agent.

### ~480 lines of copy-pasted symbol-resolution boilerplate

Twelve command files (`references`, `callers`, `implementations`, `inheritors`, `members`,
`dependencies`, `injected`, `call-chain`, `usages`, `dbset-usage`, `audit-surface`,
`audit-downstream`) contain a byte-identical ~40-line block: resolve → not-found-with-suggestions →
ambiguous-with-candidates → stopwatch → `Telemetry.Log`. Compare `ReferencesCommand.cs:15-47`
against `InheritorsCommand.cs:15-47` — identical but for the command-name string.

### Three divergent definitions of "is this a write?"

- `AuditSurfaceCommand.IsWriteApiName` — includes `SaveChanges`, `Add`, `Update`, `Remove`, `Execute*`
- `AuditDownstreamCommand.WriteApiNames` — same list **minus** `SaveChanges`/`SaveChangesAsync`
- `ImplementationComplexity.WriteCallNames` — only `SaveChanges`/`Execute*`, deliberately (see
  weirdness #4)

Plus ad-hoc `Add`/`AddRange`/`AddAsync` checks in `AuditImmutableCommand` (three sites) and a
`SaveChanges` check in `AuditCacheCommand`. `audit-surface` and `audit-downstream` are sold as the
inbound and outbound views of the same code and are the two most-used commands — they can disagree
about whether a method writes.

### The output layer models one of the four question shapes

`OutputFormatter` offers `WriteResults<T>` and `WriteMessage` — a list of locations, which is
`target.md`'s shape A. Eighteen commands use it. The eight that don't hand-roll their own
`WriteCompact`/`WriteJson` and their own `JsonSerializerOptions`: `snapshot`, `health`, `cycles`,
`surface-score`, `section-shape`, `service-map`, `audit-surface`, `audit-downstream`.

That set is not arbitrary — it is exactly the commands whose output is a member table or an
aggregate report rather than a list of locations. This is one abstraction covering a quarter of
the domain, with the other three-quarters improvising around it, not eight sloppy commands. The
Json shape is the contract agents parse.

### The four `audit-*` rule commands are four copies of one sweep

`audit-auth`, `audit-cache`, `audit-immutable` and `audit-ef` each carry the same
`foreach project / foreach document` structure (twice over, in each file), differing only in the
predicate applied and the message emitted. `CLAUDE.md`'s Phase 3 specifies the target shape
already — "rules are classes implementing a simple interface" — so this is drift from the stated
design, not merely repetition.

### Phase 2 does not exist

`CLAUDE.md` describes three phases. Phase 2 — `rename`, `inject`, `move-method`,
`remove-parameter`, `extract-interface` — is entirely unbuilt: no `Renamer`, no `DocumentEditor`,
no `TryApplyChanges` anywhere in the tree, only a `// Phase 2 — Mechanical Transform commands
(future)` comment at `Program.cs:88`. Building it is out of scope for simplification, but it
constrains two backlog items — see `target.md`, "the transform seam".

### `SurfaceScoreEngine` is a 1,569-line god class

Highest churn in the repo (22 commits). Internally organised as seven numbered passes plus
cross-cutting scorers, marked with `// ---------------- Pass N ----------------` comments. The
passes are already separable; the file just never got split. The surrounding cluster —
`SurfaceScoreCommand` (724), `SectionShapeAnalyzer` (612), `SurfaceScoreBaseline` (538),
`ImplementationComplexity` (508), `SurfaceScoreConfig` (418) — is ~4,370 lines, a third of the
codebase, and carries nearly all the churn.

### ReSharper findings worth acting on

962 issues at SUGGESTION+, overwhelmingly style noise (591 `LambdaExpressionCanBeMadeStatic`).
The non-noise set:

- `AsyncVoidLambda` + 2× `AccessToDisposedClosure` in `ServeCommand.cs:83-84,140` — the file-watcher
  debounce timer captures variables disposed in the outer `finally`, and an exception inside the
  `async void` reload lambda takes the process down rather than logging.
- `CS8604` possible-null argument at `ImplementationComplexity.cs:340`.
- 9 unused locals (`SurfaceScoreEngine.cs:741`, `CodeHealthAnalyzer.cs:95` ×2,
  `AuditSurfaceCommand.cs:581`, `AuditEfCommand.cs:50`, `HealthCommand.cs:38`,
  `ServiceMapCommand.cs:52`, `FileDependencyGraph.cs:150`, and one in tests).
- `SurfaceScoreEngine.BuildFullToReadPairs` takes a `typesByDisplay` parameter it never reads.
- 3× `EmptyGeneralCatchClause`.

## Test coverage

201 tests, green. Coverage is concentrated exactly where churn is — the surface-score cluster has
~1,900 lines of tests — and absent everywhere else.

**Well covered:** `SurfaceScoreEngine`, `SurfaceScoreBaseline`, `SolutionClassifier`,
`BuildInspector`, `CanonicalReadDtos`, `SectionShapeAnalyzer`, `DtoInventory`, `EffectiveAccessibility`,
`SymbolResolver`, `LocationHelper`, and the `references`/`callers`/`members`/`injected`/
`dependencies`/`implementations`/`inheritors`/`call-chain`/`parameters`/`audit-surface`/
`audit-downstream` commands.

**No tests at all:**

- `serve` / `stop` / `ServerClient` — the whole hot-mode path, including the routing table that
  S001 fixes. **This is the gap that let S001 ship and survive four months.**
- `snapshot` (#3 by usage) and `SnapshotAnalyzer`
- `cycles`, `FileDependencyGraph`, `StructuralAnalysis`
- `usages`, `dbset-usage`, `ownership-violations`, `service-map`
- `audit-auth`, `audit-cache`, `audit-immutable`, `audit-ef`
- `health` command wiring (`CodeHealthAnalyzer` itself has 38 lines of tests)
- `OutputFormatter`, `Telemetry`, `skill`, `install`, `request`

No dead-code, duplication, or coverage gate runs anywhere — there is no CI at all.
