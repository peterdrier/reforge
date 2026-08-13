# Section-Architecture Scoring - Design

**Date:** 2026-05-29
**Status:** Implemented; Section 1 superseded by v0.23.0 (see "Amendment (v0.23.0)")
**Target version:** v0.18.0

> **Amendment (v0.23.0) — section membership is structural, not configured.**
>
> Everything below still holds except *how a type joins a section*. Sections are no longer
> matched by config (interface lists -> path globs -> namespace prefixes -> symbol globs, with a
> namespace fallback); a type belongs to the section of its **containing assembly**, with
> `<X>.Contracts` folded into `<X>` and the solution-common prefix stripped for display.
>
> **This supersedes principle #2 for the grouping question specifically.** Assembly membership is
> neither behavioral nor nominal: it is **structural and compiler-enforced**, which is strictly
> stronger than both. Principle #2 exists because a nominal signal can be laundered by renaming —
> and the symbol globs this replaces (`Admin*`) were exactly that. You cannot rename a type into
> another assembly; moving it means changing the project file and the reference graph, which is a
> real architectural act the compiler validates. Behavioral analysis remains the rule for
> read-vs-mutation and projection-vs-primitive-read; it was never the right tool for "which
> section owns this type", because ownership is a fact about the build, not about behavior.
>
> Consequences for the sections below:
> - `SectionRule` keeps **policy only** — DTO anchors, `canonicalReadDtos`, `readShards`,
>   `requires*Surface`, `grandfatheredDependencies`, `escapeHatchReadMethods`. Its matchers
>   (`paths`/`namespaces`/`symbols`) and the `repositoryInterfaces`/`serviceInterfaces`/
>   `readServiceInterfaces` sugar are deleted.
> - `repoBacked` is derived from what the assembly **declares** (a repository interface or
>   implementation, or a DbContext), never from a config list.
> - Resource ownership (`duplicateDbSetOwner`) is the assembly of the `DbContext` declaring the
>   `DbSet`; the `resources.dbSets.ownerByName` map is deleted.
> - Every assembly-derived section is shaped, so the `missing*` and `crossSectionWriteSurface`
>   rules fire with no config present. A section that has not been extracted from the monolith
>   yet scores under the monolith's assembly — deliberately coarse until the split happens.

## Goal

Teach Reforge's `surface-score` to understand "section architecture": repo-backed
sections that should consolidate around a write/full mutation surface, a small read
surface, a primary `<Section>Info` DTO (often also the cache value), optional documented
read shards, and a single `<Section>SettingsInfo`. The engine must **reward "deleted a
service concept; the behavior is now derivable from the canonical cached DTO"** and must
**not** reward "the DTO merely got bigger" or "a read method vanished into a helper."

This is the lesson learned from the Humans `Camps` refactor, generalized so it lives
entirely in config - Reforge stays domain-agnostic.

## Non-negotiable principles (carried from prior batches)

1. **Gate, don't sum.** Surface and internal-complexity are separate axes; "improvement"
   is a Pareto improvement. The combined total is informational only.
2. **Behavioral, not nominal.** Read vs mutation, projection vs primitive read, etc. are
   decided by shape/behavior, not by parameter or type-name globs. Names are at most a
   tie-breaker or an explicit config override.
3. **No new gameable reward term.** Every additive scored term is a hill to climb. Items
   that require an unprovable judgment (derivability, "this fact belongs on the cache DTO")
   are **advisory** (zero points); only structural/behavioral facts are scored, and trades
   are policed by the baseline conservation gate.
4. **Suppressions are visible debt.** Anything exempted from a penalty still appears in the
   `section-shape` report, with reason/since/owner.

## Scope

In this batch (8 of the 9 Codex items):

- **Item 1** - `reforge section-shape` report (new command).
- **Item 2** - `crossSectionWriteSurface` scored rule.
- **Item 3** - canonical Info DTO not flagged as bag-gaming (an exemption, realized through
  the conservation gate; extends existing `CanonicalReadDtos`/`canonicalReadDtoReturn`).
- **Items 4 + 8** - derivability / cache-DTO facts, **scored structurally** via the
  read-surface budget (item 5) + policed by the baseline conservation gate; the unprovable
  derivability claim itself is advisory (zero points).
- **Item 5** - `readSurfaceProjectionMethod` scored rule (the structural scoring vehicle for 4/8).
- **Item 6** - `helperExtractionNoConceptDeleted`, **baseline-only** suspicious-improvement.
- **Item 9** - `missingReadSurface` / `missingWriteSurface` / `missingPrimaryInfoDto` scored
  rules, gated to repo-backed sections.

**Deferred:** Item 7 (parameter-bag guard strengthening). Independent of section
architecture; existing `parameterBagInput` already covers the structural case. A standing
helper-extraction penalty is also deferred (would false-positive on legitimate
parsers/builders/mappers - baseline-only catches the gaming case at the moment it happens).

## Section 1 - Config: extend `SectionRule`

*(v0.23.0: membership matchers removed — see the amendment above. The policy fields below are
current.)*

All conventions are *defaults*, overridable in `reforge.surface-score.json`. No domain
literal enters the engine. New optional fields on `SectionRule`:

```jsonc
{
  "primaryInfoDto": "CampInfo",            // default convention: "<Section>Info"
  "settingsInfoDto": "CampSettingsInfo",   // default convention: "<Section>SettingsInfo"
  "cacheDto": "CampInfo",                  // default: == primaryInfoDto; else inferred (see below)
  "readShards": [
    { "name": "ShiftsByRota", "purpose": "rota-scoped shift read model" },
    { "name": "ShiftsByUser", "purpose": "user-scoped shift read model" }
  ],
  "requiresReadSurface": null,             // bool?; default = inferred repoBacked
  "requiresWriteSurface": null,            // bool?; default = inferred repoBacked
  "requiresPrimaryInfoDto": null,          // bool?; default = inferred repoBacked
  "grandfatheredDependencies": [
    { "dependency": "PlacementService->ICampService", "reason": "legacy placement write path", "since": "2026-03", "owner": "camps-team" }
  ],
  "escapeHatchReadMethods": [
    { "method": "ICampServiceRead.MigrateLegacy*", "reason": "one-shot migration", "since": "2026-02", "owner": "camps-team" }
  ]
}
```

- **`CanonicalReadDtos`** (existing) remains the consolidation-target *set*.
  `primaryInfoDto`/`cacheDto` name the *specific* members the conservation gate watches.
- **`repoBacked`** (inferred, not stored in config): a section is repo-backed when its assembly
  declares >=1 classified `repositoryInterface`/`repositoryImplementation`, or a `DbContext`.
  `requiresReadSurface`, `requiresWriteSurface`, and `requiresPrimaryInfoDto` default to
  `repoBacked`; explicit config overrides.
- **`cacheDto` inference** (best-effort, only when not configured): find a caching-decorator
  class for the section's read interface - a class implementing the read interface whose name
  matches `Cached*` / `*CachingDecorator` / `*Cache` - and read the value type of its cache
  field/dictionary (e.g. a `Dictionary<TKey, CampInfo>` or `IMemoryCache` populated with
  `CampInfo`). If unresolved, `cacheDto` stays null and cache-fact advisories are skipped.
- **Grandfathered/escape-hatch entries are objects, not bare strings**, so suppression is
  reviewable debt. They are exempt from scoring but **always rendered** in `section-shape`.

## Section 2 - `reforge section-shape` command (item 1)

A *view*, not a score -> its own command. The engine's classification
(`Classify`/`BuildFullToReadPairs`, over assembly-derived sections) is factored into a shared
`SectionShapeAnalyzer` so `surface-score` and `section-shape` run one classification pass.

CLI: `reforge section-shape [--solution <path>] [--section <name>] [--config <path>] [--format compact|markdown|json]`.

Per section, the report prints:

- Owned repositories (interfaces + implementations).
- Full/write service interface(s) and read service interface (from `BuildFullToReadPairs`).
- Primary Info DTO; settings DTO; cache DTO (resolved or inferred, with provenance).
- Documented read shards.
- Cross-section callers using the **read** surface.
- Cross-section callers using the **write/full** surface (the `crossSectionWriteSurface`
  candidates).
- Cross-section repository / entity access.
- Likely-missing read / write surface / primary Info DTO (the `missingReadSurface` /
  `missingWriteSurface` / `missingPrimaryInfoDto` findings, gated by repo-backed).
- **Grandfathered / escape-hatch (visible debt)** - each suppression with reason/since/owner.
- **Advisory** block (Section 5): `derivableReadMethods`, `missingInfoFacts`,
  `cacheFactCandidates` - zero points.

JSON mirrors these as structured fields so an agent can consume them directly.

## Section 3 - New scored rules (surface axis)

All five rules stay **off** the `SurfaceScoreRuleGroups.InternalComplexity` set (surface axis),
get a default weight in `SurfaceScoreConfig.Default()`, and a **factual** glossary line
(no advice markers - a test scans for "use "/"prefer "/"split ").

| Rule | Fires when | Default wt |
|---|---|---|
| `crossSectionWriteSurface` | A cross-section constructor dependency on a write/full service interface that is paired with a read interface, where **all** invocations on the injected dependency are **visible and read-covered** (reuses Pass-5 `writeCapableInterfaceUsedReadOnly` invocation analysis), and the dependency is **not** grandfathered. Specializes the cross-section case of `writeCapableInterfaceUsedReadOnly`: when both would fire on the same cross-section dependency, `crossSectionWriteSurface` wins (the generic one is suppressed for that dependency to avoid double-count). Detail names the suggested read interface and the caller->callee->method. **Unknown-usage state:** if the dependency escapes analysis - stored beyond its backing field, passed onward as an argument, returned, captured in a lambda/closure, or reached via reflection/dynamic - the rule does **not** fire a confident penalty; it emits a `crossSectionWriteSurfaceUnverified` advisory + diagnostic (0 points) instead. | 15 |
| `missingReadSurface` | Section is **repo-backed** (or `requiresReadSurface: true`) and has no read-service interface. Fires regardless of whether a write/full service exists - the target architecture is read + write + primary Info for any repo-backed section. Once per section. | 10 |
| `missingWriteSurface` | Section is **repo-backed** (or `requiresWriteSurface: true`) and has no write/full service interface. Once per section. | 10 |
| `missingPrimaryInfoDto` | Section is **repo-backed** (or `requiresPrimaryInfoDto: true`) and has no DTO matching `primaryInfoDto`. Fires regardless of service-surface presence. Once per section. | 10 |
| `readSurfaceProjectionMethod` | A read-service-interface method classified **behaviorally** as projection-summary / predicate / scalar-fact / UI-builder (see classifier). A **surcharge on top of** the base `readServiceInterfaceMethod` cost. Methods in `escapeHatchReadMethods` are exempt (and shown as debt). No count threshold - every qualifying method is charged (thresholds are gameable). | 4 |

**Read-method behavioral classifier** (used by `readSurfaceProjectionMethod` and the
advisory candidates):

- **predicate** - returns `bool` / `Task<bool>`. *(charged)*
- **scalar-fact** - returns a single primitive/string/Guid/DateTime (not the Info DTO). *(charged)*
- **projection-summary** - returns a DTO that is **not** the primary Info DTO (a narrower or
  derived shape), or a collection thereof. *(charged)*
- **UI-builder** - returns a composed view DTO (returns a `*Data`/`*ViewModel`/`*PageModel`
  shape; name suffix is a tie-breaker on top of "returns a composed DTO with nested members"). *(charged)*
- **primitive read** - returns the primary Info DTO or a collection of it (GetById/GetBySlug/
  GetForYear). *(healthy - base cost only)*
- **settings read** - returns the settings DTO (`settingsInfoDto`). *(healthy)*
- **search** - *(healthy only with a real search shape)* requires BOTH a query/filter/paging-shaped
  input AND a search-hit/result output (a `*SearchHit`/`*Result` DTO, typically a collection or
  paged envelope). A method does **not** become healthy merely by taking a `string` argument or
  being named `Search*` - otherwise a projection method launders into "search" by renaming. A
  method that fails the shape test falls through to **projection-summary** (charged).

## Section 4 - Baseline conservation gate (items 4/8 scoring + item 6)

Evaluated only under `--baseline`, in `SurfaceScoreBaseline`. The gate is a decision tree on
**what happened to the removed read/service behavior** in this diff.

**Evaluation order (per scope where read/service points went down).** Helper absorption is
checked **first** - before the ambiguity bias can launder a sideways move into a
consolidation:

1. **Helper absorption?** -> `helperExtractionNoConceptDeleted` (`improvement: false`).
2. Else per removed method, run the **coverage check** (below) to assign `coverageKind` +
   `targetDto`.
3. If every removed method is covered (existing/added DTO fact or documented shard) ->
   `canonical-consolidation` (`improvement: true`).
4. Else if any removed method is **uncovered** (and not ambiguous) ->
   `capability-evaporation` (`improvement: false`).
5. Else (remaining uncertainty) -> `canonical-consolidation` under the **ambiguity bias**,
   with the ambiguous methods marked `coverageKind: ambiguous` and their facts emitted as
   advisory `missingInfoFacts`.

The three `SuspiciousImprovement` kinds:

- **`canonical-consolidation`** (`improvement: true`): read-surface points
  (`readServiceInterfaceMethod` + `readSurfaceProjectionMethod`) went **down** and the removed
  methods' facts are **covered** - already present on, or added this diff to, the primary,
  cache, **or settings** DTO, or absorbed by a **documented** read shard - and no
  *undocumented* helper absorbed them. Any Info/settings-DTO growth is **exempt** from
  bag-gaming flags (this realizes item 3). *Does not require facts to increase* - deleting
  methods whose facts already existed on `CampInfo`/`CampSettingsInfo` is the canonical good
  move. Message e.g. "read surface consolidated into CampInfo/CampSettingsInfo (-M methods)."
- **`helperExtractionNoConceptDeleted`** (`improvement: false`, item 6): read/service points
  went **down** and a **new** stateless sink absorbed them. Detection is **broad**, not one
  narrow shape: a new `internal static` / extension-method class, or any new stateless class
  (no instance fields, not DI-registered, not interface-backed), whose public methods match
  the removed methods by name **or** by obvious body/signature similarity. The concept moved
  sideways into a helper instead of being deleted. Message names the helper and moved methods.
- **`capability-evaporation`** (`improvement: false`): read/service points went **down**, the
  removed facts are **not** covered by any consolidation target (primary/cache/settings DTO,
  added facts, or documented shard) and no helper absorbed them - the capability evaporated or
  leaked to callers.

**Coverage check** (best-effort, never scored): match each removed method's implied fact
name(s) and return shape against the consolidation-target **inventories** (see
`conservationAnchors` below) - the primary, cache, **and settings** DTO member inventories,
plus documented shard methods. Settings/config reads (name-lock date, current/public year,
season settings, etc.) consolidate into `settingsInfoDto`, not the primary DTO. A fact present
in the **baseline** inventory -> `existingDtoFact`; present only in the **current** inventory
-> `addedDtoFact`. **Ambiguity bias:** when coverage is uncertain, lean
`canonical-consolidation` (do not falsely punish the good move) - **but the ambiguity must be
visible** via `coverageKind: ambiguous` + advisory `missingInfoFacts`. The whole point is to
teach the loop, so the report must show *why* a deleted method was judged safe.

**Per-method evidence.** Every baseline conservation decision emits, for each removed
read/service method, an evidence row (`targetDto` tells the agent whether the fact is camp data
or section settings - a core Camps lesson):

```jsonc
{
  "removedMethod": "Humans.Application.Interfaces.Camps.ICampServiceRead.GetCampPublicSummariesForYearAsync",
  "coverageKind": "existingDtoFact",   // existingDtoFact | addedDtoFact | documentedShard | helper | uncovered | ambiguous
  "targetDto": "primaryInfoDto",       // primaryInfoDto | cacheDto | settingsInfoDto | readShard | null
  "coveredBy": ["CampInfo.Images[]", "CampInfo.Links[]", "CampInfo.CurrentSeason.WebOrSocialUrl"],
  "missingInfoFacts": []               // {fact, targetDto} entries when coverageKind is uncovered/ambiguous
}
```

The scope-level verdict is the roll-up of these rows; the rows are the audit trail.

**`conservationAnchors` in surface-score JSON.** To make the gate immune to unrelated DTO
churn *and* auditable (coverage is checked against inventories, not inferred from score
deltas), the surface-score JSON gains a top-level `conservationAnchors` object -
**always emitted regardless of `--top-symbols`**. Anchors are **keyed by fully-qualified symbol
identity** (namespace + type/interface name, qualified by section), never by display name, so
two `CampInfo`/`ISettingsServiceRead`-style names in different namespaces cannot collide.
Each anchor carries points *and inventory*:

- read/full **interface** anchors: method names + return-type shapes (`{ name, returns }`).
- primary/cache/settings **DTO** anchors: a **recursive, path-based** member inventory, not a
  flat member list. The walk descends through canonical child DTOs and collection elements
  (using `[]` for collections), e.g. `CampInfo.Seasons[].Members[].UserId`,
  `CampInfo.CurrentSeason.RoleHolders[]`, `CampInfo.Images[]`. Bounded by a max depth and a
  visited-type set to stop cycles; only canonical/child-DTO types are descended (primitives and
  framework types are leaf facts). Without paths the gate cannot prove that membership/lead
  predicates are derivable from cached camp data.
- **documented shard** anchors: the shard name/purpose and (best-effort) attributed methods.
- per-anchor `byRule` points (so surface deltas remain exact).

The baseline comparison reads anchor inventories + points from here, never from the capped
`topSymbols` or a group-level `dto*` fallback. A baseline file lacking `conservationAnchors`
(pre-v0.18 output) falls back to `topSymbols`/group with a `baseline-anchors-missing` precision
diagnostic, and per-method `coverageKind` degrades to `ambiguous` (inventory unavailable).

## Section 5 - Advisory candidates (zero points, `section-shape` only)

Surfaced as guidance, never scored (the unprovable part):

- **`derivableReadMethods`** - read methods classified projection/predicate/scalar/UI-builder,
  each with a best-effort hint of which DTO fields would cover them and on which target DTO
  (e.g. "GetCampPublicSummariesForYearAsync likely derivable from CampInfo if it exposes
  Images, Links, WebOrSocialUrl and CampSeasonInfo exposes BlurbLong").
- **`missingInfoFacts`** - facts implied by derivable/removed methods not yet on a consolidation
  target. Each entry carries `{ fact, targetDto }` where `targetDto` is
  `primaryInfoDto | cacheDto | settingsInfoDto`. Settings/config facts (name-lock date,
  current/public year, season settings) carry `targetDto: settingsInfoDto` - non-camp-specific
  camp settings should collapse into the single settings read shape, not stay as scalar read
  methods. (This subsumes a separate `settingsFactCandidates` list.)
- **`cacheFactCandidates`** (item 8) - repo/full-service read methods answering facts that
  could live on the cache DTO; prefer adding the fact to the cache DTO over a new method.
- **`crossSectionWriteSurfaceUnverified`** - a cross-section write/full dependency whose
  read-only use could not be confirmed because the dependency escapes analysis (stored beyond
  its field, passed onward, returned, captured, or reflection/dynamic). Advisory, not the
  confident `crossSectionWriteSurface` penalty.

JSON: `advisory: { derivableReadMethods: [], missingInfoFacts: [], cacheFactCandidates: [], crossSectionWriteSurfaceUnverified: [] }`,
per section.

## Section 6 - Glossary, axis, fixtures, tests, validation

- **Glossary** (`SurfaceScoreRuleGlossary.cs`): factual one-liners for the five new rules
  (`crossSectionWriteSurface`, `missingReadSurface`, `missingWriteSurface`,
  `missingPrimaryInfoDto`, `readSurfaceProjectionMethod`).
- **Axis**: all five new rules on the surface axis (not in `InternalComplexity`).
- **Weights** (`SurfaceScoreConfig.Default()`): `crossSectionWriteSurface` 15;
  `missingReadSurface` / `missingWriteSurface` / `missingPrimaryInfoDto` 10 each;
  `readSurfaceProjectionMethod` 4.
- **Fixtures** (`test/SampleSolution/SampleSolution.Services/`): add Camps-shaped types -
  `ICampService` (write/full), `ICampServiceRead` (read), `CampInfo` (primary + cache value,
  with nested `CampSeasonInfo`/collections so the recursive path inventory is exercised),
  `CampSettingsInfo`, a projection read method, a predicate read method, **a settings/config
  scalar read method that should consolidate into `CampSettingsInfo`**, a repo-backed section
  missing a read interface, a repo-backed section missing a write surface, a repo-backed section
  missing an Info DTO, an orchestrator-only section (must NOT trip `missing*` rules), a
  `CampReadModelProjection` static helper, an `internal static` extension-style helper (to
  exercise broadened helper detection), a cross-section caller injecting `ICampService` but
  calling only reads, and a caller that **passes the injected dependency onward** (to exercise
  the unknown-usage advisory). Add a grandfathered dependency + an escape-hatch read method to a
  test config to assert suppression + visible-debt rendering.
- **Tests** (`test/Reforge.Tests/SurfaceScoreTests.cs` + a new `SectionShapeTests.cs`):
  one test per new rule; the search-shape gate (a `string`-arg method named `Search*` with a
  non-search return is still charged); the unknown-usage advisory; `section-shape` content
  (compact + JSON); `conservationAnchors` always present, keyed by FQ identity, carrying
  recursive path inventories (e.g. `CampInfo.Seasons[].Members[].UserId`) not flat lists;
  per-method evidence rows with the right `coverageKind` **and `targetDto`**; helper detection
  running **before** the ambiguity bias - **specifically, a removed read method + a new helper +
  *ambiguous* DTO coverage must resolve to `helperExtractionNoConceptDeleted` (improvement:false),
  NOT `canonical-consolidation`** (the core gaming-hole regression guard); and before/after JSON
  pairs for all three baseline
  kinds - including the regression guards for adjustment #1: "delete a read method whose facts
  already exist on CampInfo -> `canonical-consolidation`, improvement:true, `coverageKind:
  existingDtoFact`", an `addedDtoFact` case, a **settings-consolidation case** (`targetDto:
  settingsInfoDto`), an `ambiguous` case (uncovered fact surfaces as advisory), and a
  helper-move case that must NOT be laundered into consolidation.
- **Validation**: dogfood on the sample solution (CLI + `jq`); then **both** Humans main and the
  **Camps refactor/PR branch** (`timeout 420` each) - main may lack the acceptance transitions,
  so the Camps branch is where the real before/after conservation cases live (the failure we
  just debugged). Identify the branch via the Humans repo's open Camps PR. Report concrete
  per-rule numbers for each, and which rules fire where.

## Acceptance (from the Camps examples)

With Camps configured (`ICampServiceRead`=read, `ICampService`=write/full, `CampInfo`=primary
+ cache):

- `GetCampPublicSummariesForYearAsync`, `GetCampPlacementSummariesForYearAsync`,
  `IsUserCampLeadAsync`, `IsUserCampEventManagerAsync`, `GetCampLeadSeasonIdForYearAsync`,
  `BuildCampDetailDataBySlugAsync` -> charged by `readSurfaceProjectionMethod` and listed as
  `derivableReadMethods` advisories once `CampInfo` carries the facts.
- Adding role-holder IDs / members / images / links / public-detail facts to
  `CampInfo`/`CampSeasonInfo` -> `canonical-consolidation` (not bloat), even if read methods are
  deleted because the facts were already present.
- Collapsing scalar settings/config reads (name-lock date, current/public year, season
  settings) into `CampSettingsInfo` -> `canonical-consolidation` with evidence
  `targetDto: settingsInfoDto` - non-camp-specific camp settings become the single settings
  read shape rather than staying as scalar read methods.
- Moving `CampService` methods into `CampReadModelProjection` -> `helperExtractionNoConceptDeleted`
  (baseline), improvement:false.
- A cross-section caller injecting `ICampService` but only reading -> `crossSectionWriteSurface`,
  suggesting `ICampServiceRead`, unless grandfathered.
