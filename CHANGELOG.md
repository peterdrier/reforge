# Changelog

What changed and why. Newest first.

## v0.25.0 - derive canonical read DTOs from the exported Contracts surface (BREAKING config change)

v0.23.0 deleted the section-*membership* config because assembly boundaries already state it.
`canonicalReadDtos` was the same mistake one level down: a hand-authored list restating something
the code already declares. On Humans it had drifted exactly the way hand-authored lists do — three
listed names no longer existed as types at all, and 14 real DTOs went inert because their key said
`Users` and there is no `Users` assembly yet.

- **`SectionRule.CanonicalReadDtos` is deleted. No override, no escape hatch.** A section's read
  API is now derived: the *exported* data types it declares in its `<Section>.Contracts` assembly,
  or under a `Contracts/` folder in its own assembly. Both shapes occur in the wild — Humans has 26
  of the first and 15 of the second — so derivation unions them. A section with neither has not
  declared a read API, and the score says so. That absence is the point: config can no longer paper
  over a boundary that was never drawn, so the fix is to make the assembly structure right rather
  than describe it in JSON.
- **Location is never evidence of public.** A `Contracts` folder or namespace says where the author
  filed a type, not what the assembly publishes — Humans declares `internal` service interfaces in
  a `.Contracts` *namespace* today. Every candidate is gated on effective accessibility (v0.24.0's
  `SurfaceVisibility.IsExported`), read off the semantic model, never off the path or the name.
- **Resolves an inconsistency, not just a config field.** The old field was consumed two
  incompatible ways: flattened to one global set of simple names by `ScoreReturnTypeRules`, but
  section-scoped by `SectionShapeAnalyzer`'s anchor resolution. Membership now follows the declaring
  assembly like everything else, and the return-type rules match on symbol identity
  (`declaringAssembly|fullyQualifiedName`) instead of on the simple name — two assemblies may each
  declare a `UserInfo` and only one may be exported from a contracts surface.
- **The credit stays solution-wide.** A Tickets method returning Users's canonical DTO still earns
  `canonicalReadDtoReturn`. It charges for a *use* — what a method hands back — not for a
  declaration's published shape, so it follows v0.24.0's split and is not scoped or gated. What
  changed is identity, not scope.
- **The entity-leak exemption no longer depends on the credit's weight.** Setting
  `canonicalReadDtoReturn` to `0` used to silently re-enable `methodReturnsEntityAcrossSection` on
  canonical DTOs. A canonical DTO is the section's read API by definition; returning one is never a
  leak, whatever the credit is worth.
- **A config still carrying `canonicalReadDtos` is reported, not ignored.** `System.Text.Json`
  would drop the unknown key without a word — the exact silent drift this change exists to close,
  since the list used to grant credit and suppress a penalty solution-wide. `SectionRule` captures
  unrecognized members and the engine emits a `removed-config-field` warning naming each stale
  block, the same treatment `unknown-config-section` gives a stale section key.

The behavioral DTO test now counts **every shape a consumer can invoke**, not just directly declared
public ordinary methods — operators, conversions and events included, since each lives under a symbol
kind an "ordinary public method" check never sees. The property side is narrowed to match: only a
readable **instance, non-indexer** property is evidence of carrying data, exactly the filter
`DtoInventory` applies, so an admitted type always has facts to inventory rather than anchoring an
empty path set. `class SearchHit : List&lt;int&gt;` declares one property and no methods of
its own, but a consumer gets `Add`/`Remove`/`Insert` through it; an explicit interface
implementation is `private` on the class symbol yet callable by anyone who casts; a default
interface method is behavior the type never declares at all. Admitting such types would grant them
return credits, suppress entity-leak penalties, and let one win a section's primary anchor. The base
walk stops at `System.Object`/`System.ValueType`, and only **non-abstract** interface members count —
that is what keeps records in, since every record implements `IEquatable<T>`. This also tightens the
DTO-inventory descent set in `section-shape`, which shares the same test.

**`DtoInventory` walks inherited properties too.** Counting inherited data means a DTO whose
properties live on a data-only base is admitted — but the inventory that backs its anchor only read
declared members, so such an anchor carried an empty or partial path set and the conservation gate
would prove facts against nothing, never noticing an inherited fact going missing. The same gap
already applied to any `dto`-tagged type with a base class. Most-derived declaration wins, so a
shadowed or overridden property still yields one path. Two smaller exclusions come with it: indexers
(whose symbol name emits a nonsense path like `Foo.this[]`) and statics are skipped — an indexer
declared directly on a DTO was previously included, so a path can disappear here, which is the one
place anchor paths can shrink rather than grow.

Also fixed here, because deriving from declaration paths depends on it: **`LocationHelper.NormalizePath`
required only a string prefix, not a directory boundary.** With the solution at `/work/App`, a linked
source at `/work/AppContracts/Foo.cs` normalized to `Contracts/Foo.cs` — a path that never existed.
That fed classification path globs (`**/Models/**`), the new contracts-surface check, and every file
path reported to the caller as something it could open. Containment now requires the prefix to end
at a separator; a path that isn't genuinely under the solution root is returned as-is, which is what
the method already documented.

Output JSON shape is unchanged (same top-level and per-group keys, no key added or removed).

Measured on Humans, both binaries run back to back against one built tree — `typesAnalyzed` 2,840
and `internalComplexityTotal` 3,111 on both sides, 46 sections: `surfaceTotal` 14,956 -> 14,880
(-0.5%), 24 of 45 scoring sections unchanged. (A `Humans.Shifts` extraction is in flight there, so
the absolute totals are a snapshot; the deltas are not.) Three rules moved, and the movement is the
finding:

- `canonicalReadDtoReturn` -3 -> -81. Nine config blocks listed 34 DTO names between them and
  granted the credit exactly once. Derivation credits eight sections, led by Application (-24) and
  GoogleIntegration (-21) — neither of which had a config block at all.
- `missingPrimaryInfoDto` 240 -> 110. Fourteen sections resolve a primary anchor through the derived
  set that the `<Section>Info` convention missed. Calendar moves the other way, 0 -> 10: its
  configured canonical DTO `CalendarEventInfo` is an `internal sealed record` under `Services/Dtos/`,
  so Calendar publishes no read API and the config had been asserting one no consumer can reach.
- `readSurfaceProjectionMethod` 116 -> 248. The projection surcharge only fires for a section with a
  resolved primary anchor — without it a primitive read can't be told from a projection. The newly
  anchored sections therefore reveal projection debt that had been invisible (Governance +40,
  Expenses +32).

## v0.24.0 - score the assembly's effective public surface (BREAKING score change)

Since v0.23.0 a section IS an assembly, which makes "public surface" a compiler-enforced fact
rather than a judgement call: it is what the assembly exports. The corpus didn't reflect that.
`SolutionClassifier` admitted every non-private type, and the scoring passes checked only
**declared** member accessibility — nobody computed **effective** accessibility. So a `public`
method on an `internal` class scored full surface points, as did a `public` type nested inside an
`internal` one, for API no other section can call.

That inverted the incentive. The cheapest way to improve the score was to make things internal
without changing anything a consumer sees, while genuinely encapsulating a type — a real
decoupling win — was invisible.

- **Surface = effectively public.** A declaration scores on the surface axis only if it and every
  type containing it is `public`, walked to the outermost declaration. `protected` does not count
  (it's reachable only by deriving, and members have always required `public`), and
  `InternalsVisibleTo` does not widen surface — a test project seeing internals doesn't make them
  product surface.
- **Sizing is untouched.** Internal types stay in the corpus and `longMethod`, `largeClass`,
  `cognitiveComplexity` and the dispatcher rules still score them in full. A well-encapsulated
  section now reads as small surface plus whatever complexity it actually carries, instead of being
  penalised for having an implementation. `internalComplexityTotal` is unchanged by construction.
- **Declarations are gated; uses are not.** Rules charging for a declaration's published shape
  (DTO/service/repository/controller members, method-shape, return-shape, boundary-input,
  `oneImplementationInterface`, `readSurfaceProjectionMethod`) skip anything not effectively public.
  Rules charging for a **use** do not: `crossSectionRepository`, `crossSectionFullService`,
  `crossSectionReadInterface`, `sameSectionReadService`, `writeCapableInterfaceUsedReadOnly`,
  `crossSectionWriteSurface`, `duplicateDbSetOwner`, `diRegistration`. An internal class injecting
  another section's repository still forces the assembly reference and still calls across the
  boundary — gating those would have made coupling free and recreated the exact gaming this change
  exists to close, one rule family over.
- **Conservation anchors track exported surface only.** The Plan C gate diffs interface method
  names between baseline and now, so an internal interface in the anchor set meant deleting one of
  its methods later read as capability evaporation — for surface that scores zero and no consumer
  can reach. Anchors now cover exported interfaces and exported methods. On Humans that is
  125 -> 90 anchors and 816 -> 477 anchor methods: 42% of what the gate policed was unreachable.
  `SectionShape.ReadServiceInterfaces` / `FullServiceInterfaces` and the `missing*` rules still see
  the unfiltered lists, so shape detection and the section-shape view are unchanged.
- **Every baseline from before v0.24.0 is incomparable.** Surface totals fall by construction, so a
  `--baseline` comparison against an older file reports a large fake improvement. Re-baseline
  deliberately. Output JSON shape is unchanged — only values move.

Measured on Humans (built): `surfaceTotal` 55,716 -> 21,010 (-62.3%), `internalComplexityTotal`
3,131 -> 3,131 (0), `typesAnalyzed` unchanged. Both repository-implementation rules went to exactly
zero, which is real: all 41 of Humans' repository implementations are `internal` behind a public
interface, so the old score charged 8,250 points for surface that doesn't exist. Extracted sections
go dark as intended (Camps -95%, Events -97.5%, Calendar -97.9%), while six all-public assemblies
moved by exactly zero — `Application` alone is now 47% of the solution's surface. That is the honest
picture: Humans' published API lives in its shared contracts assembly, not in its sections.

Known gap, not fixed here: an `internal` MVC controller's actions now score nothing, because
controllers are reached by reflection rather than an assembly reference. Consistent with the
definition, but it under-counts HTTP surface.

## v0.23.0 - sections are assemblies (BREAKING config change)

`surface-score` grouped types with a config cascade: per-section interface-name lists, then
path globs, then namespace prefixes, then symbol-name globs, first match wins, with a
namespace-segment fallback. In Humans that cascade had grown to 883 lines re-describing
boundaries the solution already states — every new section was a manual config edit that
mis-grouped **silently** when forgotten, and the symbol globs (`Admin*`) were exactly the
nominal matching the scoring design's own principle #2 warns against.

A type's containing assembly says the same thing, but structurally: the compiler enforces it,
it cannot drift from the solution, and it costs zero config.

- **Grouping is now the containing assembly.** `<X>.Contracts` folds into `<X>` (a contracts
  assembly is the published face of its section, not a section), and the dot-segment prefix
  shared by every assembly is stripped for display, so `Humans.Store` reports as `Store` —
  the same group names the config used to produce. Test projects are still excluded. Grouping
  reads `ContainingAssembly`, not the enumerating project, because a project's compilation
  also sees referenced projects' source types.
- **Hard cut, no fallback.** `SectionRule.Paths/Namespaces/Symbols`, the per-section
  `repositoryInterfaces`/`serviceInterfaces`/`readServiceInterfaces` sugar, the legacy `groups`
  array, and the namespace fallback are **deleted**. A not-yet-extracted section now scores
  under whatever assembly it lives in (`Application`, `Infrastructure`, `Web`, `Domain`, `UI`).
  That is coarse and intentional: per-section visibility comes back automatically as each
  section becomes its own assembly, instead of being asserted by config that may be a lie.
- **What to do with your config:** delete the `sections` block's matchers (or the whole block).
  Stale keys are ignored, not fatal — an old config still loads, it just no longer groups.
  `sections` now carries **policy only**, keyed by the assembly-derived section name:
  `primaryInfoDto`/`settingsInfoDto`/`cacheDto`, `canonicalReadDtos`, `readShards`,
  `requires*Surface`, `grandfatheredDependencies`, `escapeHatchReadMethods`. `classifications`,
  `weights`, and the allowed-shape/dispatcher escape hatches are unchanged.
- **Ownership is derived, not declared.** `resources.dbSets.ownerByName` is gone:
  `duplicateDbSetOwner` now takes a table's owner to be the section of the `DbContext` that
  declares its `DbSet`, and fires on any class outside that section touching it. A DbSet
  declared by two contexts in different sections has no single owner and is skipped rather
  than attributed arbitrarily. Same for repo-backing: a section is repo-backed when it declares
  a repository or a DbContext. Both were hand-maintained lists before.
- **Section shapes cover every section**, not only configured ones — so the `missing*` and
  `crossSectionWriteSurface` rules now fire with no config file at all. Expect new points on a
  default-config run; set a rule's weight to `0` to silence it.
- **JSON output shape is unchanged** (`groups[]`, `byRule`, totals, `topSymbols`,
  `conservationAnchors`, …). `configuredSections` keeps its key and its type but now lists the
  solution's assemblies. Humans' `pr-surface-report.py` parses it unmodified.
- Sample solution split into real section assemblies (`SampleSolution.Camp` +
  `.Camp.Contracts`, `.Lodge`, `.Dorm`, `.Tent`, `.Reporting`) so the tests exercise assembly
  grouping and the `.Contracts` fold rather than a synthetic config.
- Fixed alongside: the test fixture treated only a `.git` *directory* as a repo root, so inside
  a git worktree it silently opened the main checkout's sample solution.
- Fixed alongside: `System.Text.Json` assigns brand-new dictionaries through the `Sections` /
  `Classifications` / `Weights` setters, discarding the `OrdinalIgnoreCase` comparers from their
  field initializers — so a config key differing only in case silently never matched. Latent
  before, but it bites now that section names are derived from assembly names instead of being
  defined by these very keys (`"camp"` no longer reaches `Camp`). The three maps are re-keyed
  case-insensitively at load, ahead of the defaults merge so a case-variant override replaces
  the default rather than sitting beside it.
- Fixed alongside: type dedup keyed on the fully qualified name alone, but every project's
  compilation reaches its references' source types, so the key had to be per ASSEMBLY. Two
  assemblies may legitimately declare the same fully qualified name (an internal helper); the
  second was dropped from scoring, and an assembly whose every type collided vanished from the
  section map. Dedup is now `assembly|displayName`; the three display-name indexes downstream
  became first-wins rather than throwing on the now-possible duplicate.
- Every type-lookup map in the scoring passes is now keyed by `SolutionClassifier.TypeKey`
  (declaring assembly + fully qualified name) instead of the name alone — `typesByDisplay` in
  both the engine and the section-shape analyzer, `readByDisplay`, the full→read `pairs` map, and
  the inline-parameter-object site counter. Keeping both same-named types in `classified` was only
  half the fix: the maps then collapsed them again, so a consumer of its own assembly's
  `Shared.IOrderService` could resolve to the *other* assembly's declaration and make the
  dependency, return-type, DI, and write-surface passes report false cross-section findings or
  suppress real ones. Keying on the *declaring* assembly keeps cross-assembly lookups correct: a
  consumer in A injecting B's type resolves through B's key, because the symbol it holds is B's.
- Fixed alongside: `canonicalReadDtos` were imported from EVERY config section, including keys
  naming an assembly that no longer exists. Canonical names apply solution-wide, so a stale key
  kept granting `canonicalReadDtoReturn` credit and suppressing `methodReturnsEntityAcrossSection`
  everywhere — the opposite of the "policy for a section that doesn't exist is inert" contract.
  Now restricted to live sections, and a new `unknown-config-section` **warning** names any policy
  block that matches no assembly, so a mis-keyed or stale block is visible rather than silently
  dropping its DTO anchors, overrides, and grandfathered debt. That matters for the migration this
  release forces: config keys used to *define* sections and now have to match derived names.
- Fixed alongside: stripping the shared prefix could land two different assemblies on one
  section name (`Company.Product` falls back to its last segment while `Company.Product.Product`
  strips to its tail — both `Product`), silently pooling two unrelated assemblies' scores into
  one group. Post-strip collisions now fall back to the full folded name; the guard keys on the
  folded name so the intended `X` + `X.Contracts` collapse is untouched.

## v0.22.0 - guard --baseline against a build-state mismatch

`surface-score` gives materially different totals for the same commit depending on whether the
solution had a real `dotnet build` first (v0.21.0 traced this to cross-section/DI/entity-return
rules under-resolving on an unbuilt workspace), but `--baseline` never checked whether the
baseline and the current run were even measured in the same build state. A baseline captured on
an unbuilt worktree compared against a freshly-built current run reported a uniform "regression"
that was really the measurement changing under it — this burned a real refactor run and is why
issue #9 exists.

- `SurfaceScoreBaseline.Compare` now reads the baseline JSON's `build` block (`degraded`,
  `appearsUnbuilt`, added in v0.20.0/v0.21.0) and compares it against the current run's
  `BuildHealth`. A baseline that predates the `build` block (pre-v0.20) is treated as an
  **unknown** state, never as implicitly clean — comparing it against any known current state
  counts as a mismatch, same as comparing two known-but-different states.
- A mismatch adds a `warning` diagnostic (`baseline-build-state-mismatch`) naming both states,
  e.g. "baseline was captured on a degraded/unbuilt workspace (appearsUnbuilt=true) but the
  current run compiled cleanly; the comparison may be off by several percent, concentrated in
  crossSection*/diRegistration/methodReturnsEntity rules." Surfaced in Compact/Markdown (existing
  diagnostics rendering) and as a new `build.diagnostics`-style entry in JSON's `diagnostics`.
- The comparison is **not refused** — the user may legitimately want it. Instead the verdict is
  marked low-confidence: `lowConfidence=true` in Compact, a `**LOW CONFIDENCE**` marker in
  Markdown, and an additive `baseline.lowConfidence: true` in JSON. All three are omitted
  entirely when the build states match, so a matched comparison (both clean or both degraded)
  produces byte-identical output to before this change.
- Suggestions (1) resolution-confidence signal and (2) unbuilt-workspace detection from issue #9
  were already shipped (v0.20.0–v0.21.0); (3) an in-process `--build`/`--ensure-built` flag was
  declined as out of scope — reforge is a read-only one-shot CLI, shelling out to `dotnet build`
  belongs in the caller's workflow, not the tool.

## v0.21.2 - partial types report their full source size

Follow-up to v0.21.1 (resolves the known follow-up noted there). The per-type `Lines` metric
measured only one of a partial type's declarations — whichever survived dedup — so multi-file
partial types were undercounted. That understated the size component of the composite score and
could let a large partial slip under the small-type gate (`< 5 methods && < 50 lines`).

- `Lines` now sums the line spans of every declaration of the type, skipping generated `obj/`
  declarations so it stays "total source lines" (consistent with the collection-time filter).
  Non-partial types are unaffected — they have a single declaration.
- Regression test: the sample two-file `PartialHealthFixture` (an 11-line part + a 6-line part)
  now reports 17 lines; before the fix it reported 11.

## v0.21.1 - partial types no longer crash health analysis

`code-health` / `surface-score` threw `ArgumentException: Syntax node is not within syntax
tree` whenever it analyzed a partial type whose declarations span multiple files and that
cleared the small-type gate (5+ methods or 50+ lines). Cohesion (LCOM) analysis anchored a
single `SemanticModel` to one of the type's syntax trees, then called `GetSymbolInfo` on
identifiers from method bodies living in the *other* files — which Roslyn rejects outright,
aborting the whole analysis.

- `ComputeCohesionAsync` now resolves the correct `SemanticModel` per method body
  (`model.Compilation.GetSemanticModel(body.SyntaxTree)` when the body lives in a tree other
  than the anchored model). Methods declared in secondary partial files now contribute to the
  cohesion graph instead of crashing it.
- Regression test + a two-file `PartialHealthFixture` in the sample solution exercise the
  cross-tree path; the test fails (with that exact `ArgumentException`) without the fix.
- Known follow-up, pre-existing and not addressed here: the per-type `Lines` metric still
  measures only one partial declaration, so size-based scoring undercounts multi-file partial
  types.

## v0.21.0 - surface the degraded-build errors

When `surface-score`'s MSBuildWorkspace load doesn't compile cleanly it marks the result
degraded — but until now it reported only a count ("6 compile error(s)") with no file, line,
CS-code, or message. The warning was unactionable: you couldn't tell a genuine code break from
a workspace-load gap (a source generator reforge didn't run), and you couldn't fix it. This
closes that pure observability gap — the raw Roslyn diagnostics were already in hand at the
spot that counted them; they were just thrown away.

- `BuildInspector` now RETAINS the error-severity diagnostics it was only counting: per error
  it captures id (CSxxxx), severity, project, solution-relative file path, line/column, and
  message. Diagnostics are deduped on `(id + file + line + message)` and ordered deterministically
  by `(project, file, line)`.
- The counts (`compilationErrorCount`, `unresolvedReferenceCount`) are unchanged — they remain the
  raw compiler totals. The listed detail is deduped, so the list can be shorter than the count
  (two identical errors at the same line collapse to one); this is intentional noise reduction.
- All three formats surface the errors when degraded:
  - **JSON** — additive `build.diagnostics: [{ id, severity, project, file, line, message }]` and
    `build.diagnosticsTruncated`. The existing `build.*` fields are untouched. Both new keys are
    OMITTED on a clean build — no new output when nothing's wrong.
  - **Compact + Markdown** — each error under the existing warning, one per line:
    `  CSxxxx  <path>:<line>  <message>  (<project>)`, with a `(+N more)` footer when truncated.
- New `--max-build-diagnostics <n>` (default 25; 0 = unlimited) caps the listed detail. The cap
  only bounds the list; it never affects the counts.
- This is read-only reporting: scoring numbers (total, byRule, per-group) are unchanged. Whether
  the residual errors are a reforge load bug or a genuine break is now a diagnosable follow-up —
  you can finally see them.

## v0.20.0 - conservation gate

Under `--baseline`, surface-score now classifies what happened to read/service behavior
that was removed in a diff, per section. Why: the Pareto gate already catches a surface
drop bought with complexity, but it can't tell a real consolidation ("deleted a read
method; its fact already lived on the canonical DTO") from gaming ("the method moved
sideways into a helper") or harm ("the capability evaporated"). This is the lesson from
the Humans Camps refactor, generalized.

- Three `SuspiciousImprovement` verdict kinds per section: `canonical-consolidation`
  (improvement) when every removed method's fact is covered by the primary/cache/settings
  DTO inventory or a documented shard; `helperExtractionNoConceptDeleted` (not an
  improvement) when a NEW stateless sink absorbed a removed method; `capability-evaporation`
  (not an improvement) when a removed fact is uncovered. Helper absorption is checked FIRST,
  so a sideways move can't be laundered into a consolidation by ambiguous coverage.
- Coverage is checked against the Plan B `conservationAnchors` inventories (recursive DTO
  member paths + interface method lists), never inferred from score deltas — so the gate is
  immune to unrelated DTO churn and is fully auditable. Settings/config facts consolidate
  into `settingsInfoDto`, camp data into `primaryInfoDto`/`cacheDto`.
- Per-method evidence rows (`conservationEvidence` in `--format json`):
  `{ removedMethod, coverageKind (existingDtoFact|addedDtoFact|documentedShard|helper|
  uncovered|ambiguous), targetDto, coveredBy, missingInfoFacts }` — the audit trail behind
  each verdict. Ambiguous coverage leans toward consolidation but is surfaced as advisory.
- New additive `helperCandidates` key in `--format json` (stateless sink classes + their
  public methods) so the gate can diff baseline vs current to find a NEW helper.
- A baseline JSON predating `conservationAnchors` (pre-v0.19) degrades coverage to ambiguous
  with a `baseline-anchors-missing` precision diagnostic rather than guessing.
- No new scored rules: the gate emits improvement verdicts and zero-point advisories only,
  policing trades rather than adding another gameable hill to climb.

## v0.19.0 - section architecture

surface-score now understands section architecture: it resolves each configured section's
shape (owned repositories, read/full service interfaces, primary/settings/cache DTOs,
documented read shards, charged read methods, cross-section use) and scores five new
surface-axis rules from it. A new `section-shape` command renders the full shape for an
agent. Why: the prior rules saw individual symbols, not the read/write/DTO contract a
section is supposed to keep; these rules make a leaking or incomplete section boundary
visible, and the conservation anchors give Plan C's gate a stable identity to hold a
refactor to.

- New shared `SectionShapeAnalyzer` consumes the Plan A `SolutionClassifier` output and
  resolves, per configured section: read/full interface pairing, primary/settings DTO
  anchors (explicit config, the `<Section>Info` / `<Section>SettingsInfo` convention, or a
  `canonicalReadDtos` fallback so a plural section name like `Camps` still resolves the
  singular `CampInfo`) with recursive member-path inventories, cache DTO (configured,
  inferred from a `Cached*`/`*Cache`
  decorator's cache-field value type, or default-to-primary), charged read methods (via
  the behavioral `ReadSurface` classifier), missing surfaces (gated to repo-backed via
  `SectionFacts`), and cross-section write-surface use with escape analysis.
- Five new surface-axis rules (weights): `crossSectionWriteSurface` (15) when a class in
  one section injects another section's write/full interface but every observed call is
  read-covered and the dependency does not escape analysis; `missingReadSurface` /
  `missingWriteSurface` / `missingPrimaryInfoDto` (10) for a repo-backed section lacking
  that surface; `readSurfaceProjectionMethod` (4) per read-interface method that returns a
  projection/predicate/scalar/composed view instead of the section's primary Info DTO.
- Escape analysis: when an injected cross-section write dependency is passed onward,
  returned, captured, or otherwise escapes, the read-only verdict is unconfirmed, so a
  `crossSectionWriteSurfaceUnverified` advisory diagnostic is emitted instead of a
  confident penalty. The confident cross-section penalty suppresses the generic
  `writeCapableInterfaceUsedReadOnly` for that pair. Grandfathered dependencies
  (`grandfatheredDependencies`) and escape-hatch reads (`escapeHatchReadMethods`) are
  exempt from the section penalties as documented visible debt.
- New additive `conservationAnchors` key in `--format json`: per section, each canonical
  DTO (FQ-keyed, with recursive member paths) and each read/full interface (with method
  signatures + attributed surface points). Always emitted, independent of any top cap.
- New `reforge section-shape` command (Compact / Markdown / JSON): renders each section's
  resolved shape plus advisory candidates (derivable reads, missing info facts,
  cache-answerable facts, cross-section unverified).
- Behavioral, not nominal: read-method charging and cross-section read-cover are decided
  by return/call shape and escape analysis, never by name globs. All five rules are
  penalties or surcharges; advisories are zero-point.

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
