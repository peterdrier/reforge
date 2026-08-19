# Scoring Alignment - Design Spec

## Status

Draft (2026-08-15). Design session output; **no code changes proposed for immediate
implementation**. Addresses issue #19 ("Rework the internal-complexity axis to penalize LLM
defaults, not size") and extends it with four areas #19 does not cover: contracts-assembly
weighting, project-graph proliferation, a test axis, and the design of the unattended cleanup
loop these are ultimately for.

Supersedes nothing. Amends issue #19's proposed direction in two places, both recorded under
"Amendments to issue #19".

**Every number in this spec is either (a) measured and cited, or (b) explicitly marked as a
hypothesis requiring measurement before it earns a weight.** That distinction is the discipline
this spec exists to establish; violating it is what produced the rejected duplication rule.

## Context

The surface axis is solved. #10 moved grouping to assembly boundaries, #13 restricted scoring to
effectively-public declarations, #14 derived canonical read DTOs from the exported contracts
surface. Surface is structural and compiler-enforced, and config carries policy rather than
structure.

The internal axis is not solved, and issue #19 measured why. Against Humans `14a2760` with reforge
0.25.0:

| Rule | Points | Cheapest edit that satisfies it | Better code? |
|---|---:|---|---|
| `longMethod` | 1,468 | extract N single-caller private helpers | no |
| `cognitiveComplexity` | 837 | same | no |
| `largeClass` | 435 | split into `FooPart1`/`FooPart2`, or a partial | no |
| `mutationModeParameter` | 330 | split into two real methods | **yes** |
| `actionDispatcher` | 40 | polymorphism | **yes** |
| `flagsControlFlow` | 16 | polymorphism | **yes** |

**2,740 of 3,126 internal points (88%) are satisfiable by extract-method** — the most reflexive
LLM refactoring move, and one that usually makes code worse by scattering logic across
single-caller helpers. The tool currently pays points for the fragmentation it exists to catch.

Two further problems surfaced in design review on 2026-08-15, neither covered by #19:

- **The `.Contracts` fold hides project proliferation.** v0.23.0 folds `<X>.Contracts` into `<X>`
  for section grouping — correct for the surface axis, and it stays. But it means a section that
  is two assemblies reports as one, and no reforge output anywhere names the second. Humans has
  20+ contracts assemblies where a handful are justified. They are currently invisible.
- **Test code is not scored at all.** `SolutionClassifier` drops every project matching `*Test*`
  (`SolutionClassifier.cs:23`). Test mass is the single largest uncovered surface in a tool whose
  stated job is sanity-checking LLM-generated code in a PR.

## Goal

Make every scored rule satisfy one property: **the cheapest edit that improves the score is an
edit that improves the code.** Rules that fail this are retired or re-based, not reweighted.

The score's consumer is ultimately an unattended agent running daily for six months. A rule with a
cheap degenerate fix will be found and exploited, because that is what gradient-following does.

## Rule-admission gates

Every scored rule — existing or new — must clear both gates. These are stated as acceptance
criteria in issue #19; this spec makes the first one **executable** rather than a review promise.

### Gate 1 — anti-gaming, enforced by fixture

For each scored rule, `test/SampleSolution/` carries a pair:

- a `Before` type that fires the rule, and
- a `CheapestFix` type — the laziest edit that satisfies the rule, as an LLM would perform it.

A test asserts **the score does not improve** between them. Where a rule has a legitimate good
fix, a third `GoodFix` type asserts the score *does* improve.

A rule that cannot be given such a fixture does not ship. `longMethod` would have failed this gate
on the day it was written.

Fixture constraint, from `docs/simplify/as-is.md` weirdness #5: **`SampleSolution` has no
`PackageReference` anywhere**, deliberately, because adding one degrades MSBuildWorkspace loads and
breaks the build-health tests. Test-axis fixtures therefore declare local `FactAttribute` /
`TheoryAttribute` types rather than referencing xunit.

### Gate 2 — measure before weighting

Compute the candidate signal against Humans, inspect the distribution, and **manually read the top
10 hits for false positives**, before deciding whether it earns a weight and what the weight is.

Recorded precedent: a cross-file duplication rule was proposed, investigated, and dropped. Type-2
clone detection over Humans `src/` (1,690 files, 92,891 content lines) found **zero exact clones
≥40 lines**, and only **1,564 of 92,891 lines (1.7%)** inside any cross-file 15-line structural
clone. The widest cluster was `*DbContextFactory.cs`, EF design-time boilerplate EF requires one of
per context. A rule at 1.7% coverage whose top hits are boilerplate trains readers to ignore it.
**Duplication is investigated-and-rejected; do not re-propose it as a scored rule.** If wanted at
all it is a `reforge clones` diagnostic, not an axis.

**Gate 2 has since been discharged for both candidate signals below** — feature envy and the
single-caller private helper — plus the `publicWriteSurface` transform proposed in #35. Results,
distributions and the required manual top-10 reads are recorded in
[`2026-08-19-internal-axis-signal-measurements.md`](2026-08-19-internal-axis-signal-measurements.md).
All three are recommended *against* in the form they were proposed, which is what the gate is for. The
manual reads did work the aggregates could not: 0 of the top 15 single-caller helpers is the artifact
that signal is meant to detect, and 10 of the 47 `fullServiceInterface` classifications publish no
write capability at all. Read that file before writing any of these rules.

## Prerequisites

None of this is safe to build on, and the cleanup loop is not safe to run at all, until three
things land.

1. **`S001` — one command registry.** With a hot server running, `reforge surface-score`,
   `snapshot`, `cycles` and `section-shape` print the root help text and **exit 0**.
   `ServeCommand`'s registration list was last updated 2026-04-13; `TryRelayAsync` reports success
   for any completed socket round-trip and the server nulls stderr. An unattended agent reads exit
   0 with empty output as "no findings". Verified live 2026-08-14.
2. **`S004` — hot-path round-trip tests.** The entire hot-mode path has zero tests, which is why
   S001 shipped and survived four months. The fix without the ratchet just resets the clock.
3. **Issue #17 — refuse to score a degraded build.** Currently a warning. Every measurement in
   this spec, every baseline comparison, and every night of the cleanup loop is invalid on a
   degraded build. The changelog already records one re-measurement that landed on a broken tree
   (3,723 compilation errors) and moved a single rule by 2×.

Two more should land before new rules are added, not after:

- **`S007`** — split `SurfaceScoreEngine`'s seven passes into files. It is 1,569 lines and the
  highest-churn file in the repo. This spec adds passes; doing so first avoids a 2,500-line file.
  File split only, no `IScoreRule` abstraction (`target.md`, "deliberately not done").
- **`S011`** — make the output layer model all four question shapes. The new commands below are
  shape D, exactly the improvising quarter. Building them first adds two more hand-rolled
  `WriteCompact`/`WriteJson` pairs to the eight that already exist.

## Decisions on the existing internal-axis rules

Issue #19's acceptance criteria require a recorded keep/reweight/retire decision for each.

### `cognitiveComplexity` — **keep, re-base the unit**

The signal is real; the unit is wrong. Complexity is measured **per syntactic method**, so moving
lines into a single-caller private helper reduces the number while making the code worse.

Re-base it: compute cognitive complexity for each public entry point **over its private call-tree
closure within the declaring type** — the method plus every private method in that type reachable
only from it. Extracting a single-caller helper then moves the number by exactly zero. The only
ways down are removing branching or moving logic onto another type, both desirable.

Cheapest satisfying edit: reduce real branching, or move logic to the type that owns the data.
Both improve the code. Gate 1 passes.

This is the highest-value single change in the spec: it converts the largest rule from gameable to
ungameable without inventing a new signal.

### `longMethod` — **retire as a distinct rule**

Once complexity is closure-based, closure LOC is the same measurement with a worse resolution.
Retain LOC as a reported metric on the closure (useful context in a report) but stop scoring it
separately. Retiring it also removes the double-charge where one long method scores on both rules.

### `largeClass` — **retire LOC-based, replace with cohesion**

A 900-line class is not a smell; a class doing three unrelated jobs is. LOC-based `largeClass` is
satisfied by splitting into `FooPart1`/`FooPart2` or a `partial`, which changes nothing.

Replace with a cohesion-cluster rule: score the number of **disjoint member clusters** in a class
(fields/properties that are never touched by the same method form separate clusters). A `partial`
split does not change the cluster count. Splitting *along a cluster boundary* does — and that is
the good fix.

**This reuses code that already exists and is wired to nothing.**
`CodeHealthAnalyzer.ComputeCohesionAsync` computes exactly this and is consumed only by the
`code-health` command (`HealthCommand.cs:39`), never by scoring.

### `mutationModeParameter`, `actionDispatcher`, `flagsControlFlow` — **keep unchanged**

These are the 12% that already work. They are design-smell rules rather than size rules and they
are the model for everything else. Per #19, grow this family rather than inventing new ones;
Shifts alone holds 150 of `mutationModeParameter`'s 330 points.

### Axis rename — **`internalComplexity` → `implementationShape`**

The axis has no `internal`-accessibility gate; it is a fixed rule-name partition
(`ImplementationComplexity.cs:18-27`, routed at `SurfaceScoreEngine.cs:1557`), so public code
scores on both axes. The current name misleads, and README's "small surface + whatever complexity
it actually carries" reinforces the wrong reading. Rename the scalar, the JSON key, and the README
sentence together. **Breaking JSON change** — see Compatibility.

## New: contracts-assembly multiplier

**Replaces** the binary `redundantContractsAssembly` project rule proposed earlier in design
review. The multiplier is strictly better and subsumes it.

### Rationale

A `<X>.Contracts` assembly exists for exactly one reason: so a consumer can reference the contracts
without referencing the implementation. That makes anything declared there the most durable surface
in the solution — the hardest thing you own to change. Charging more per member is not a heuristic
nudge; it is an accurate model of change cost.

### Definition

Surface-axis **declaration** rules — the durable-surface family (`publicDtoType`, `dto*Property`,
`readServiceInterfaceMethod`, `fullServiceInterfaceMethod`, `repositoryInterfaceMethod`,
`controllerAction`, `applicationServiceMethod`) — are multiplied by `contractsSurfaceMultiplier`
when the declaring assembly is a contracts assembly (the `<X>.Contracts` sibling that
`AssemblySections` already folds into `<X>`).

**Use** rules are not multiplied. `crossSectionRepository`, `crossSectionFullService`,
`crossSectionReadInterface`, `writeCapableInterfaceUsedReadOnly`, `crossSectionWriteSurface`,
`duplicateDbSetOwner`, `diRegistration` charge for coupling, and coupling to `IFooService` costs
the same wherever `IFooService` is declared.

Implementation-complexity rules are not multiplied — a contracts assembly should hold no
implementation.

### Why this is the strongest anti-gaming rule in the system

The cheapest edit that lowers the score is moving a member from `Foo.Contracts` to `Foo`.

- If some consumer genuinely references `Foo.Contracts` without `Foo`, **that move does not
  compile**, so the member stays.
- If every consumer already references `Foo` too, the move compiles — and the split never bought
  anything for that member.

**The compiler is the arbiter.** The score applies pressure; the build decides what can actually
move. There is no edit that games this rule and still builds. Gate 1 passes by construction, and
the fixture pair is trivial to write.

### Emergent consequence

Squeeze members out and a contracts assembly trends to empty, at which point deleting the project
is obvious and needs no rule. This is why the binary project rule is dropped: it is the limit case
of the multiplier.

### Design review decision, recorded

**Contracts assemblies are not to be merged with each other.** An earlier proposal to cluster them
by consumer set and consolidate 20 into 3–4 was rejected by the owner on 2026-08-15: contracts
assemblies are the exceptional case, and however many survive the multiplier's pressure is the
right number. Whatever count results is derived, not a target anyone picked — the same principle
as deriving canonical read DTOs rather than listing them. Do not re-propose consolidation.

### Implementation notes

- The information already exists and is discarded. `SolutionClassifier` computes the
  `<X>.Contracts` → `<X>` fold and does not retain which side a type came from. This is a flag on
  `ClassifiedType` plus a multiplier at weight lookup.
- `AddEntryByName` (`SurfaceScoreEngine.cs:1539`) is the single funnel where axis routing happens
  but does **not** carry the symbol's declaring assembly. The multiplier must be applied upstream,
  at the call sites that hold a `ClassifiedType`.
- The multiplier is a config weight (`contractsSurfaceMultiplier`), disable-able by setting `1`.

### Measurement obligation

v0.24.0 established that Humans' published API lives in its shared contracts assembly rather than
in its sections (`Application` alone is 47% of surface). A multiplier will therefore move a large
fraction of 14,809 surface points. Before choosing the number, measure:

1. Total surface delta and per-section re-ranking at multipliers of 1.5, 2, 3.
2. **Whether contracts work would win the cleanup loop's priority queue every night.** A high
   multiplier starves every other kind of debt. This is an interaction with the anti-starvation
   design below, not a separate concern.

## New: project graph

Humans moved from 5 horizontal layers to 100+ projects. Build times degraded materially. Many of
the new projects hold interfaces whose purpose is breaking cycles in the DI/project graph.

**Do not penalize project count generally.** It collides with "a section is an assembly" — the
surface axis rewards extracting a section into its own project, and it should.

Three findings instead, none of them a raw count:

### `launderedProjectCycle`

For each near-empty assembly, contract its edge into the assembly holding its sole implementation
and re-check the project DAG for a cycle. **If a cycle appears, the split is not decoupling
anything — it is laundering a bidirectional dependency into an invisible one.**

The fix is reversing a dependency direction, not moving project files. This is the one project-level
finding the contracts multiplier cannot express, because a hidden cycle is a graph fact, not a
per-member weight.

### Critical-path depth

For build time, the metric is the **longest chain in the project DAG**, not the node count. 100
projects in a wide flat graph parallelize; a 12-deep chain serializes and no core count saves you.
Report depth and the critical path itself.

### Leaf-shim count

Correcting the point above for one subclass: contracts assemblies and interface shims are
near-leaves — they depend on nothing, build in parallel, and add no critical-path depth. Their cost
is **fixed per-project MSBuild overhead** (evaluation, restore, up-to-date check, assembly write),
paid on every build, times N. For leaf shims, count *is* the cost. Report it separately from depth.

### Reuse and risk

`FileDependencyGraph` and `StructuralAnalysis` already implement SCC and betweenness centrality over
the 1,690-file graph. The same algorithms over a ~100-node project graph are nearly free.

**Risk:** per `as-is.md`, `cycles`, `FileDependencyGraph` and `StructuralAnalysis` have **zero
tests**. Budget for covering them before building on them.

## New: test axis

The observed problem is not bad tests, it is **test mass without test value** — five tests added
per PR regardless of what the PR does.

### Headline rule — net-new behavioral coverage

For each test method, compute the set of production symbols in its call closure. A test whose set
is a **subset of the union of the other tests' sets** adds constraint on refactoring without adding
verification.

Score it the way `canonicalReadDtoReturn` works — the only credit in the current system:

- a small **cost** per test method (a test is coupling to implementation; it is durable surface in
  the sense that matters — it is what makes refactoring expensive), offset by
- a **credit** per production symbol it is the first to cover.

A test earning its keep nets negative. Five tests hitting one method with different literals net
positive and appear as debt. Cheapest satisfying edit: delete the redundant test. Gate 1 passes.

### Supporting rules, descending confidence

- **Identical-closure siblings** — tests in one class with identical call closures differing only
  in literals. Fix: collapse to `[Theory]`/`[InlineData]`. Strong Gate 1.
- **Assertion-free tests** — no assertion in the closure, or `Assert.True(true)`.
- **Mock-verification-only tests** — every collaborator mocked and the only assertion is a
  `Verify(...)`. These assert the implementation rather than the behavior, which is precisely what
  makes refactoring expensive and what reforge exists to reduce.

### Reported, not scored

Test:production LOC ratio per section. Too blunt to gate on, but it is the number that makes the
situation visible at a glance.

### Consequence for `SolutionClassifier`

Test projects are currently excluded at `SolutionClassifier.cs:23`. They must become a **third
corpus** with its own axis, not folded into the production corpus — a test class must never
contribute to `surfaceTotal`.

## New: fan-in / dead exported surface

Design review resolved two opposite readings of the same measurement. Both are true; they point
different directions and both are useful.

Let `externalCallers(m)` = distinct symbols outside the declaring assembly referencing `m`.

- **0** → `unusedExportedSurface`. Real penalty. The member should never have been exported. Fix:
  `internal`, or delete.
- **1** → `singleConsumerSurface`. Small penalty — a private conversation held in public.
- **≥2** → **no penalty.** Record fan-in as **risk metadata, not score.**

The high end is a governor, not a target: it is what tells the unattended loop *not* to refactor
the thing 200 files depend on. Penalizing it would make the loop avoid exactly the shared code that
most needs care.

Composition worth noting: a contracts member with zero external consumers is hit by both this and
the contracts multiplier — maximum publishing cost for zero delivered value, which is correct.

**Cost:** solution-wide reference finding is expensive. Compute in the nightly run only, never in
the per-PR path.

## Amendments to issue #19

### The six read-surface rules — **do not retire yet**

#19 proposes cutting `crossSectionReadInterface`, `readServiceInterfaceMethod`,
`readSurfaceProjectionMethod`, `writeCapableInterfaceUsedReadOnly`, `crossSectionWriteSurface` and
`canonicalReadDtoReturn` (1,230 points, 7% of Humans' 17,935) as the best value-per-line cut.

Amend: **keep `canonicalReadDtoReturn`.** It is the only negative weight in the system. An
all-penalty score teaches an agent to delete rather than to design, and the cleanup loop needs at
least one gradient that points toward building something good. Cut the config-heavy parts instead
(`readShards`, `escapeHatchReadMethods`), and treat "add a second credit" as open work — candidates:
returning a canonical DTO, and moving a method onto the type whose data it uses.

`crossSectionFullService` (2,248 pts, #2 rule solution-wide) stays, per #19. It is the coupling
signal and it is already assembly-derived.

### The shortcuts axis — **deferred, not rejected**

Design review proposed a third axis of LLM-tell rules: suppression growth (`#pragma warning
disable`, `!`, `[ExcludeFromCodeCoverage]`, `dynamic`), swallowed exceptions, dropped
`CancellationToken`, fake async, net-new single-caller private helpers, parallel implementations.

**Owner assessment 2026-08-15: these are not currently observed in Humans.** Deferred. Recorded
here so the reasoning survives: every rule in the family has the property that the cheapest fix is
deleting the shortcut, so the family is worth building *if the symptoms appear*. Re-evaluate rather
than re-derive.

The one member worth keeping in scope now is **net-new single-caller private helper**, because it is
the direct counterweight to any surviving size-based rule and is meaningless as a stock — it only
exists as a delta.

## The cleanup loop (consumer context — not reforge's design)

**This section is FYI, not a reforge work item.** The loop is a Humans-side skill; its design
belongs to that repo and to its owner. It is recorded here only because it is the demanding
consumer these scoring changes are aimed at, and because two of its needs are genuine reforge
requirements rather than nice-to-haves:

- **Per-section density, not absolute points**, must be derivable from the report — otherwise no
  consumer can rank sections fairly. `typesAnalyzed` exists; a per-section size denominator does
  not. **Delivered since (#44/#45):** every group now carries a `metrics` block with `locProd`,
  `files`, `classes`, `interfaces`, `methods` and both complexity distributions, plus a
  solution-level rollup, and `--list-groups` carries `locProd` for every section. This requirement
  is met.
- **A named-rule-family view** must be reportable, so a consumer can target one family per run
  rather than "reduce the total". `byRule` already carries this; the family grouping does not exist.

Everything else below is the consumer's problem, sketched so those two requirements have context.

The destination: a skill running daily against Humans, guiding continuous incremental change
largely unattended over ~6 months.

### Selection and anti-starvation

Rank by **debt density** (points per KLOC), never absolute. `Application` is 47% of surface; ranking
by absolute points means it wins every night forever and small sections are never visited.

```
priority(section) = debtDensity(section) × (1 + daysSinceLastVisit / 14)
```

Every Nth night, force the **least-recently-visited** section regardless of score. This is a
guarantee, not a tiebreak.

### Memory

`.reforge/debt-ledger.json`, committed to the Humans repo: per section, last visit, what was
attempted, points moved, and the human verdict on the resulting PR. Over 180 nights the ledger is
the durable artifact — more valuable than any individual PR, and the input to weight review.

### Gates

Most already exist (per-axis Pareto, conservation anchors, helper-extraction detection). Add:

- clean build before **and** after (requires issue #17)
- tests green
- **test count must not increase** — the loop is forbidden from adding tests
- **project count must not increase**
- diff cap: ~400 changed lines, ~15 files. One concept per night.
- the targeted rule family named up front, with before/after JSON attached to the PR

### Anti-Goodhart

The loop optimizes the score; the score is a proxy. Three mitigations:

1. **One rule family per night, named in advance.** An instruction to "reduce the total" makes the
   loop find the cheapest global gradient, which is always the most gameable rule.
2. **Gate 1 fixtures in CI** are what stop degenerate minima from being reachable at all.
3. **No auto-merge**, at minimum for the first month.

### Weights — do not autotune

A loop that tunes the weights it is scored against is a closed system with no ground truth, and it
will correctly discover that all-zero is extremely efficient. **Weights change only from human
accept/reject evidence recorded in the ledger, reviewed monthly by the owner.**

Note the mechanism is untouched and available: per `as-is.md` weirdness #6, Humans'
`reforge.surface-score.json` (826 lines) leaves `weights` **empty**, so every Humans figure ever
measured is at default weights.

### Cadence

Run daily; open a PR only when the run finds something above a value threshold. Expect cheap wins
to be exhausted in weeks 3–6, after which "nothing worth a PR" is the healthy majority outcome and
the ledger is the product.

## CI performance

Not scored work, but it gates the loop. The PR scoring action is reported as very slow. Likely
causes, in order: two full `MSBuildWorkspace` loads (baseline + head) over 100+ projects, cold
restore, no caching.

Fix: score once per commit on the default branch and publish the JSON as a SHA-keyed artifact; the
PR job computes only the head side and fetches the base side. Should roughly halve it. The residual
cost is ~100 project evaluations before Roslyn starts — which is what the project-graph work above
addresses. The two problems are the same problem.

## Sequencing

1. `S001` + `S004` + issue #17 — correctness blockers for anything unattended.
2. `S007` + `S009` — split the engine by pass; add the CI that does not exist; land Gate 1 fixtures
   there, retroactively for every surviving rule.
3. **Contracts multiplier** — smallest change, strongest anti-gaming story, information already
   computed.
4. **Project graph** — active pain, small graph, reuses existing SCC code, immediate build payoff.
5. Closure-based complexity + cohesion-based god class.
6. Test axis.
7. Fan-in / dead surface (nightly only).
8. The cleanup loop, on a scoring system by then worth automating against.

## Compatibility

**Scores are not comparable across reforge versions, and that is not a goal.** Owner decision,
2026-08-15: the only comparison that matters is pre/post a change to the *codebase*, measured with
one reforge version on both sides. Nobody tracks a Humans score longitudinally across reforge
upgrades.

This materially loosens what the changes below have to preserve. Earlier releases (v0.24.0,
v0.25.0) documented total movements as breaking and told readers to re-baseline deliberately; that
caution was aimed at the wrong risk. Weights, rule sets, and axis definitions may change freely.
What must hold instead:

- **Same-version comparability is absolute.** A baseline and a current run must be produced by the
  same reforge version — otherwise the comparison silently measures the tool rather than the code.
  `SurfaceScoreBaseline.Compare` already guards the analogous build-state mismatch (v0.22.0); it
  should guard version mismatch the same way, as a `warning` diagnostic plus `lowConfidence`, not a
  refusal.
- Renaming `internalComplexity` → `implementationShape` changes a top-level JSON key. Humans'
  `pr-surface-report.py` parses this output and must be updated in the same change. This is a
  consumer break, which still matters; a score movement is not.
- New axes are additive JSON keys. New commands are new keys, not changes to existing ones.

## Tests

- Gate 1 fixture pairs for every scored rule, existing and new (see Gate 1).
- Closure-based complexity: a fixture where extracting a single-caller helper leaves the score
  **unchanged**, and one where removing a branch lowers it.
- Cohesion god class: a fixture where a `partial` split leaves the cluster count unchanged, and one
  where splitting along the cluster boundary lowers it.
- Contracts multiplier: a fixture pair where moving a member `Foo.Contracts` → `Foo` lowers the
  score, and one where a contracts-only consumer exists so the same move would not compile.
- Project graph: a fixture with a laundered cycle, one with a justified contracts split, one with a
  redundant one.
- Test axis: fixtures using locally-declared `FactAttribute`/`TheoryAttribute` — **no
  `PackageReference`** (`as-is.md` weirdness #5).
- Coverage for `FileDependencyGraph` / `StructuralAnalysis` / `cycles` before the project graph
  builds on them.

## Deliberately not done

- **No cross-file duplication rule.** Investigated and rejected at 1.7% coverage; see Gate 2.
- **No merging of contracts assemblies with each other.** Owner decision, 2026-08-15.
- **No automatic weight tuning.** Closed loop with no ground truth.
- **No penalty on raw project count.** It contradicts the assembly-is-a-section model. Depth and
  leaf-shim count instead.
- **No `IScoreRule` abstraction** when `SurfaceScoreEngine` is split. File split only, per
  `target.md`.
- **No shortcuts axis yet.** Deferred on owner assessment; see Amendments.
