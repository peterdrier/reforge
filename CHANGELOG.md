# Changelog

What changed and why. Newest first.

## Unreleased - Gate 2 measurements for the internal-axis candidate signals

`docs/superpowers/specs/2026-08-19-internal-axis-signal-measurements.md`. Discharges the
measure-before-weighting obligation the 2026-08-15 scoring-alignment spec sets for the two candidate
signals #19 proposes, and answers both open questions #35 flags for the `publicWriteSurface`
transform. Measured against Humans: 44 sections, 3,448 types, 157,860 prod LOC.

**All three signals are recommended against in the form they were proposed**, which is the gate
working rather than failing. One deletion is recommended and is free.

- **Single-caller private helper.** 706 of 1,444 non-public methods have exactly one caller — a
  **48.9% base rate**. As a stock this is not a smell detector, it is a tax on decomposition, and it
  would point an agent at inlining private methods back into their callers. Confirms the spec's
  existing position that the signal is meaningful only as a net-new delta, and supplies the evidence
  for why: the stock is half the population.
- **Feature envy.** 385 candidates as specified, of which **184 (47.8%) are mappers** — and for a
  mapper, the refactor the rule implies (move it onto the type it reads) is a dependency inversion:
  the entity would depend on its own projection. A manual read of the non-mapper top 15 finds five
  more mappers the structural test missed. A refinement does work — non-mapper, returns a
  scalar/bool/enum/string, synchronous — landing at **26 candidates at roughly 70% precision**, with
  the residue a nameable class (`Render*` / `Format*` / `Display*`). Recommended for one more round,
  not for a weight.
- **`publicWriteSurface`.** The mean hid the shape: 19 of the 24 sections that declare a write
  interface declare exactly one, so the distribution is effectively binary plus a single outlier
  (Users, with 16). That forces a choice #35 leaves implicit — **per section** ("this section
  publishes write capability") prices something `fullServiceInterfaceMethod` does not, while **per
  interface** measures the same dimension more coarsely *and* contradicts the crossed-the-line
  rationale by charging one section sixteen times for one decision. The per-section reading is the
  defensible one and is not what a naive implementation produces. Either way the weight cannot be
  calibrated here: past the binary split all discriminating power is n=1. Recommended as
  **reported, not scored** until a second corpus says whether that distribution is a property of
  Humans or of sectioned codebases.
- **Retiring `crossSectionWriteSurface` is free.** It scores 0 across all 44 sections, so its
  `crossSectionSuppress` set is empty in consequence and deleting rule, set and branch is a measured
  no-op.

Two standing figures also moved and are recorded: the internal axis is still **87% size rules**
(reproducing #19's 88% at a newer commit with a newer reforge), and the six read-surface rules #19
proposes retiring have **grown** from 7% to **11.8% of surface**.

The spec's Gate 2 section now points at the measurements, and its cleanup-loop requirement for a
per-section size denominator is marked delivered — #44/#45 added exactly that.

## Unreleased - A DTO's published shape includes what it inherits

#29 (3b). `ScoreDtoSurface` iterated `c.Type.GetMembers()`, which does not return inherited
members, so a DTO was charged only for the properties it declared. Moving them up to a base class
whose name matches no DTO pattern therefore zeroed the charge — and changed nothing a consumer can
see, which is the whole test a surface rule has to pass.

The hole had two depths, and fixing only the first would have left the cheaper one open:

1. **Hoist some properties.** The derived type keeps one of its own, still looks like a data
   carrier, still pays `publicDtoType` — but pays per-property only for what it declares.
2. **Hoist all of them.** The type's own public property count drops to zero, so
   `LooksLikeDataCarrier` stops recognising it as a DTO at all and `publicDtoType` disappears too.
   Strictly cheaper than (1) and strictly more effective.

Both are closed. Property scoring and the data-carrier check now walk the base chain, stopping at
`object`, at the first base declared outside the solution (a framework base's properties are not
this section's surface to withdraw), and — for scoring — at a base that is itself a separately
scored DTO, which already pays for its own. A property redeclared in a derived type is charged
once, and an inherited entry says where it came from:
`Title (inherited from ReportEnvelopeBase)`.

One consequence in the other direction, and it is correct: a type inheriting behaviour is no longer
a pure data carrier, because a consumer can reach it. That covers inherited public **methods**,
inherited public **events** (subscribing is calling), and non-abstract **default interface
methods** — behaviour the type never declares anywhere, so no walk over declarations can see it.
Declared behaviour has always disqualified a type; inherited behaviour is not different.

**The predicate is now an allowlist**, and that change is the substantive one. It used to ask "which
member shapes are behaviour?" and reject those — a framing that lost four times in a row under
review: ordinary methods, then inherited events, then non-abstract default interface methods, then
explicit interface implementations (which Roslyn reports as `private` while anyone who casts can call
them, so both an accessibility filter and an interface scan miss them). Each miss silently published
a behavioural type as DTO surface. It now asks the closed question instead — is every member carried
data, or invisible to a consumer? — reusing `CanonicalReadDtoSet`'s `IsCarriedData` and
`IsInvisibleToConsumers`, so an unrecognised member shape disqualifies by default and the next shape
nobody thought of fails safe.

One deliberate difference from `CanonicalReadDtoSet.IsDataCarrier` survives: this walk **stops at the
solution boundary**. Delegating wholesale measured **+5 points on Humans**, all of it one EF
migration class — `ExpenseLineProofRows : Migration` — admitted as published DTO surface because EF's
`Migration` base declares public properties. A framework base's members are not this section's
surface to withdraw. That the other predicate has no such stop is filed as #49 rather than changed
from here, because it decides canonical read DTOs and reopens a judgment call the fix would have to
settle.

**Measured on Humans: no change at all** — surface 17,379 and internal 3,113 before and after, with
no type changing classification in either direction, including after the allowlist rebuild. The
corpus contains zero DTO-shaped types with a base class, so nobody has taken this path. That is the
useful reading: the fix is preventive, it closes a Gate 1 hole before it is walked through, and it
costs no score churn to adopt. The sample solution carries the fixtures that do move
(`InheritedDtoFixtures.cs`), and three of the four new tests fail without the fix.
## Unreleased - Cognitive complexity: charge the structure, name the code

Two findings from the dogfooding read in #31, both about `cognitiveComplexity` describing a member
badly rather than about what it charges for.

**A nested function at a member's own top level no longer pays a nesting level.** The walker added
one for every lambda and local function, which is right per SonarSource when the function sits
inside other logic. It is not right when the function *is* the member body. `System.CommandLine`
takes an action delegate, so all 29 of this tool's commands put an entire body inside
`command.SetAction(async (parse, ct) => { ... })` — and every branch in there cost 1 more than the
same code written as a method body, purely because the API takes a delegate instead of letting you
override a method. The rule is now: charge the level only when there is an increment-bearing node
between the nested function and its member.

Measured before and after:

| | cognitiveComplexity | internal axis |
|---|---:|---:|
| Reforge (`Reforge.slnx`) | — | 1,843 → **1,684** |
| Humans (`Humans.slnx`, 44 sections) | 820 → **771** | 3,162 → **3,113** |

No other rule moved on either corpus, and surface is untouched on both — this is an
internal-axis-only change, which is what it should be.

The exemption is for the **outermost** nested function only, and getting that wrong is easy: the
exempt body is walked at nesting 0, so a lambda declared inside it sees 0 as well and would take the
exemption a second time — a LINQ lambda inside a `SetAction` callback scoring its branches at member
depth though it is genuinely two functions deep. "Already inside a nested function" is therefore
tracked separately from the nesting value, saved and restored around each one, so it describes the
path to the current node rather than how many the walk has seen. Several sibling lambdas at a
member's own top level are each exempt; one nested inside another is not.

The divergence from SonarSource is deliberate and worth stating plainly: a *top-level* LINQ lambda
containing a branch now costs one less than Sonar would charge, because it too has no
increment-bearing node above it. The alternative — special-casing "the lambda is the whole member
body" — is narrower but brittle, keyed on the shape of the enclosing statement rather than on
anything structural. Nesting depth is the thing being measured, and at nesting 0 there is no depth.

**The entry points at the code now, not at the signature.** A charge read
`Create (CC 94) (AuditEfCommand.cs:17)`, where line 17 is the method signature and the branching is
in an anonymous delegate further down with no name and no reported location. For a tool whose
premise is that an agent can act on the output without a follow-up `Read`, sending the reader to a
line the code is not on is the wrong answer even when the charge is right. When a nested function
holds a strict majority of a member's score, the entry now reports that function's line and says so:
`Create (CC 75, 75 in a nested function) (AuditEfCommand.cs:21)`. A member whose complexity is
spread across several lambdas still reports against the member — there is no single place to point
at. 11 of Reforge's own entries re-attribute; the charge is unchanged in every case.

`ImplementationComplexity.CognitiveDetail` returns the breakdown; `Cognitive` stays as it was for
every existing caller.

## Unreleased - Per-section size/complexity metrics beside the surface score

`surface-score` reported three numbers per section — `total`, `surfaceTotal`,
`internalComplexityTotal` — and nothing about the code they describe. That is not enough to read a
delta with. A section's surface points fall when its API shrinks *or* when its code is deleted; its
internal-complexity points fall when methods get simpler *or* when they move somewhere else. #19
documents the sharper version of the problem: most internal-complexity points are satisfiable by
edits that don't improve the code, so a score delta without a size delta beside it is a number a
consumer can't act on. `snapshot` already computed all of this — but solution-wide, for the history
CSV, which is the wrong grain for anything that ranks or compares sections.

Each group now carries a `metrics` block (`locProd`, `files`, `classes`, `interfaces`, `methods`,
cognitive + cyclomatic avg/p95/max with the method holding the max, `maxClassLoc` with its class),
plus a solution-level rollup. Compact and markdown print LOC and a cognitive figure per section
inline; JSON carries the whole block.

**Both complexity metrics, for different reasons.** Cognitive is what the internal axis actually
scores, so a section's `cognitiveComplexity` points and its cognitive p95 move together and can be
read against each other. Cyclomatic is what `snapshot` has always recorded solution-wide, so a
section's number is comparable to the history series it sits inside. They are the same walk over
the same methods, so carrying both costs one field each.

**The corpus is the scoring corpus, not the solution's file set.** Metrics are re-aggregated from
the same `ClassifiedType` list the rules run over, which fixes the grain question the obvious
implementation gets wrong: measuring files-on-disk per project would report growth the score has no
way to explain. Consequences, all deliberate: no test LOC (test projects never enter the classifier,
and attributing them to a section needs project-reference resolution — #36/#37 territory);
generated code excluded exactly as the internal axis excludes it; complexity measured only over
methods that have a body, because folding a 0 or a 1 in for every abstract declaration would drag a
section's average toward whichever number the bodyless case produced.

**Informational, and load-bearingly so.** The pass adds no score entries, so totals are
byte-identical to before it existed — verified by diffing full `--format json --all` output across
the change with the `metrics` keys stripped, not only asserted. Graph metrics (reach, core SCC,
cycles, fan-out) stay out: they are global by nature, and a per-section subgraph variant is a
design question, not an aggregation.

`ImplementationComplexity.Cyclomatic` now holds the McCabe walk that lived privately in
`SnapshotAnalyzer`, so the history series and the per-section number are one implementation rather
than two that agree until one is edited.

Measured against Humans (`Humans.slnx`, 3,448 types, 44 sections) before and after those five
corrections, the metrics move and the score does not:

| | before | after |
|---|---:|---:|
| `locProd` | 160,291 | **158,049** |
| `files` | 1,695 | **1,695** |
| `methods` | 5,425 | **5,523** |
| `surfaceTotal` / `internalComplexityTotal` | 17,379 / 3,162 | 17,379 / 3,162 |

2,242 net lines moved: 2,431 lines (1.5% of the corpus) of generated code were leaking in through
partial types whose handwritten half happened to be the classifier's primary file, against 189
lines of linked-file copies that were being dropped from the rollup; 98 method bodies — constructors,
implemented partial methods, explicit interface implementations, operators and finalizers — were
missing from both distributions.

Three corrections from review, all of which the metrics pass made newly load-bearing:

- **Deconstructing `foreach` was never counted.** `foreach (var (k, v) in xs)` parses as
  `ForEachVariableStatementSyntax`, a *sibling* of `ForEachStatementSyntax` rather than a subtype,
  so the McCabe switch — which matched only the latter — undercounted every one of them by 1. The
  cognitive walker has always handled both, which is what makes this a slip rather than a policy.
  Matching `CommonForEachStatementSyntax` covers the pair. This also corrects `snapshot`'s
  cyclomatic figures on any solution that deconstructs in a loop; the history series shifts up
  slightly at the point of this commit.
- **Generated code is filtered per declaration, not per primary file.** A partial type is one symbol
  spanning several files, so testing the classifier's primary file decided the whole type: a
  handwritten class with a generated `.Designer.cs` half leaked the generated LOC and methods in
  when the handwritten file happened to be primary, and dropped the handwritten half when it did
  not. Each declaring syntax reference and each method is now filtered by its own tree.
- **Constructors are in the sample.** The rollup filtered to `MethodKind.Ordinary`, which dropped
  every constructor — while `snapshot` has always sampled `ConstructorDeclarationSyntax`. A
  constructor that branches carries the same implementation cost as a method that does, so
  excluding them both understated a section and made the cyclomatic figure incomparable with the
  series it is meant to sit beside.
- **Membership is stated as an exclusion, not an allowlist.** The sample first filtered to
  `MethodKind.Ordinary`, then to Ordinary-plus-constructors, and each revision was still wrong for
  a kind nobody had thought of — explicit interface implementations next, and operators and
  finalizers behind them. An allowlist has to be right about every kind that can carry an
  implementation, and every one it misses drops real code with no signal that anything is absent.
  The rule now admits any member that declares a body and names the two things that are not written
  implementation: property/event accessors (their bodies belong to the property) and
  compiler-synthesized members.
- **Implemented partial methods reached the sample at all.** A partial method is two symbols: the
  defining declaration `partial void M();` is what `GetMembers()` returns and it has no body, while
  the implementation hangs off `PartialImplementationPart`. Taking the first declaring reference on
  the symbol in hand therefore found a bodyless declaration and dropped the method entirely.
  Resolving through the implementation part, and preferring whichever declaration carries a body,
  covers that and the partial-type ordering case with it.
- **A distribution with samples always names the method holding its max.** The max was tracked
  with a strict comparison against a 0 seed, so a section of straight-line code — every cognitive
  score 0 — never updated the name, and reported `max: 0` held by nobody. Ties now go to the first
  method sampled, which is deterministic because the classified corpus is.
- **A linked file counts once per section it compiles into.** The same physical source file can be
  linked into two projects, which compiles it into two assemblies and so into two sections — two
  real copies, each of which the sections counted. The solution rollup deduplicated by path alone,
  so it dropped the second copy's LOC while still counting its classes and methods: the rollup
  stopped being the sum of its sections. Humans has four such copies (locProd 157,860 → 158,049).
- **`--list-groups` covers sections that scored nothing.** A section whose types are all unscored
  has metrics and no `GroupScore`, so enumerating only the scored groups dropped it — precisely the
  section a size-ranked listing needs to show. The listing (and a new `sections` array in the JSON)
  now spans every section; `discoveredGroups` still means what it always did.

## Unreleased - Gate 1 tranche 4: the two cross-section dependency rules

Pairs for `crossSectionReadInterface` and `crossSectionFullService`, the first fixtures to use the
satellite-section mechanism. Backlog 22 → 20. Both fail, and for unrelated reasons.

**`crossSectionFullService` (8 → 0): the rule only reads constructor parameters.** The cheapest fix
moves the injection point from a constructor parameter to a settable property. Same dependency, same
assembly reference, same call, same boundary crossed — and the design is worse, because a
constructor parameter is a compile-time statement that the object cannot exist without the
dependency and a settable property is a null reference at whatever moment someone forgot.

`ScoreDependencyUse` iterates `c.Type.Constructors` and nothing else, so property injection, setter
injection, service location and a method parameter are all free of *every* rule in the
dependency-use family. `crossSectionFullService` is just the cheapest rule to demonstrate it with —
`crossSectionRepository` pays five times better for the identical edit.

**`crossSectionReadInterface` (2 → 0): duplication is free.** The cheapest fix stops injecting Camp's
read interface and copies the one calculation into the consumer. Nothing charges for a private
method, and nothing anywhere charges for the fact that the same logic already exists one assembly
over.

This is the awkward shape: the rule is directionally right and gameable anyway. Reaching for another
section's *read* API is the good version of a cross-section dependency — narrow, published, through
the owner — which is why it is priced at 2, the cheapest charge in the config. An agent clearing
charges in weight order arrives here last and pays nothing to delete the most defensible dependency
in the codebase. Raising the weight makes it worse: the more the dependency costs, the more the
duplicate pays.

**Not fixtured, with reasons recorded in the backlog list.** `crossSectionRepository` needs a
`*Repository` in its satellite, and satellites compile into `SampleSolution.Gate` in the full
solution — which would make Gate repo-backed and switch the `missing*` rules on across every other
fixture's neighbourhood. That needs a harness change, not a fixture. `crossSectionWriteSurface`
measured 0/47 on Humans; a fixture that fires it is easy to write and would say nothing about why it
never fires in the field.

## Unreleased - Gate 1 tranche 3: all three dispatcher rules are gameable

Before/CheapestFix pairs for `actionDispatcher`, `mutationModeParameter` and `flagsControlFlow`.
Backlog of uncovered rules: 25 → 22. **All three fail the gate**, and they are recorded as findings
rather than tuned into passing.

| Rule | Cheapest fix | Score | Backstop |
|---|---|---|---|
| `actionDispatcher` | inline the delegated arms | 62 → 36 | `mutationModeParameter`, at 25 vs 41 |
| `mutationModeParameter` | enum selector → `string` | 35 → 10 | none |
| `flagsControlFlow` | `[Flags]` → one bool per flag | 22 → 21 | 11 of 12 points |

Three separate problems, not one:

**`actionDispatcher` prices the two folds in the wrong order.** Its cheapest exit is not to un-fold
the operations but to delete the three named private members and paste their bodies into the switch
arms. The fold survives; what is destroyed is the part a reader could still see. The backstop does
fire — `mutationModeParameter` picks the method up — but charges 25 where the structural rule
charged 41. Between two methods hiding the same three operations behind one enum, the one that
delegates to named members is the better of the two, and Reforge prices it 16 points worse. Fixing
this means re-basing the two rules against each other, not raising either: while a structural
dispatcher outprices an inline one, deleting the structure pays.

**`mutationModeParameter` has no backstop for an untyped selector.** Replacing the enum with a
`string` loses every property the enum carried — the legal set, misspelling rejection, exhaustive
switch checking — and the score improves by 25 with nothing charging anything. The rule tests for
`TypeKind.Enum`, which is precise about the shape it was written for and blind to the shape you get
by taking the type away. The gap wants a rule for a mutation branching on a string parameter.

**`flagsControlFlow` fails by one point, and that is not a weight bug.** A flags enum and three
bools are the same design; neither is worth arguing for. The right amount for an agent to gain by
moving between them is zero, and any weight that makes one strictly cheaper only picks which
direction gets gamed. Recorded as directionless rather than mispriced.

## Unreleased - genericActionDispatcher folded into actionDispatcher

`genericActionDispatcher` is gone as a rule. Its three distinguishing conditions are now surcharges
on `actionDispatcher`.

It required four things at once: a body that switches and routes arms to distinct members, *and* a
generic verb name (Apply/Handle/Process/Execute/Create/Save), *and* an action/mode enum parameter,
*and* not looking like a state machine. On a 2,842-type corpus it fired **zero** times, while its
two component smells fired 40 (`actionDispatcher`) and 330 (`mutationModeParameter`). A rule that
never fires is not a strict rule, it is an absent one — and worse, it was the *expensive* rule, so
the shape it was meant to price was being billed at the cheap rate the whole time.

The fix is to stop gating on the conjunction. `actionDispatcher` fires on the structural condition
alone, as before, and adds:

| Surcharge | Why |
|---|---|
| +8 typed selector | an action/mode enum: the fold is visible in the signature |
| +8 generic verb | the name tells you nothing about which operation runs |
| +10 application service | the folded door is the application's own API |

A dispatcher is now priced by how many of these it has instead of being free of all of them until it
has every one. The plain 3-arm dispatcher that scored 25 still scores 25; the shape
`genericActionDispatcher` was written for scores 51 — close to the 48 it would have charged, but now
it can actually be reached.

`actionDispatcher` also inherits the interface propagation that only `genericActionDispatcher` had.
A structural dispatcher declared on an interface is a contractual smell, not merely an
implementation one, and that was never specific to generic-verb names.

[Flags] enum parameters are excluded from the typed-selector surcharge, consistently with
`mutationModeParameter`: a flags argument is a set of independent toggles, not a selector between
operations, and `flagsControlFlow` owns that smell.

**Migration.** Delete any `genericActionDispatcher` weight from a config file — it is no longer a
known key. Baselines that recorded it will show it drop to zero and `actionDispatcher` rise; on a
corpus where it measured zero, only `actionDispatcher` moves, and only for dispatchers that earn a
surcharge or are declared on an interface.

**New diagnostic: `unknown-config-weight`.** A weight key that names no rule is now warned about
instead of being silently ignored. This is the weights-table twin of `unknown-config-classification`
and exists because retiring a rule is exactly what leaves an inert number behind in someone's
config. A test asserts that no *default* weight key trips it, so the diagnostic can never start
firing on a solution that ships no config.

## Unreleased - A satellite contracts assembly costs double

Every surface charge on a declaration in a `<Section>.Contracts` assembly is now multiplied by
`contractsAssemblyMultiplier` (default **2**). The same type under a `Contracts/` folder inside the
section's own assembly is charged once.

The difference is reach. A `Contracts/` folder in `App.Shifts.dll` is only reachable by referencing
`App.Shifts.dll` whole. `App.Shifts.Contracts.dll` can be referenced on its own, by anyone, with no
dependency on the implementation — which is the point of the shape, and also what makes it the
hardest surface a section can ever withdraw. Wider reach, higher price.

`CanonicalReadDtoSet.IsOnContractsSurface` already told the two shapes apart internally and then
collapsed them into one `bool`; the multiplier is the first thing to care about the difference.

Two exclusions, both deliberate:

- **Credits are never scaled.** Doubling a negative would make publishing pay, inverting the rule.
- **The internal-complexity axis is never scaled.** It is the counterweight to surface and has to
  keep one unit everywhere it is measured.

A multiplier of `<= 1` is a no-op rather than an error. `0` in particular must not silently delete
the charge: a config typo should weaken a rule, never erase the surface it measures.

**Reporting.** `ScoreEntry` now carries `origin` (`main` / `contracts`) and `multiplied`, and
`GroupScore` reports `mainSurfaceTotal` and `contractsSurfaceTotal` beside `surfaceTotal`. The
origin is recorded even when the multiplier is off — it describes where the symbol lives, not
whether it was scaled. Compact output marks scaled entries `[contracts]` and puts the split on the
group header; Markdown marks them `_(contracts)_`; JSON carries both fields. Without that, a reader
seeing a 10-point DTO property cannot tell a doubled 5 from a weight change.

Sections still fold their contracts assembly in (`App.Shifts` and `App.Shifts.Contracts` are both
section `Shifts`) — the fold is right for section *identity*, and the origin is what the fold used
to discard. This is the first half of the per-origin table in #37; the tests column depends on #36.

**Baselines taken before this change will show a jump** in any section with a satellite contracts
assembly. That is the intended repricing, not a regression, but a conservation gate run across the
boundary will report it as one.

The `CampStayEntity` / `CampLegacyEntity` sample pair is renamed to `CampStaySummary` /
`CampLegacyStay`. The `Entity` suffix was load-bearing only for the retired entity classification;
with that gone the names implied a rule that no longer exists. The pair itself stays — it is the
positive and negative case for the canonical derivation, one type on the contracts surface and one
off it, which is also now the fixture for this multiplier.

## Unreleased - Retired: methodReturnsEntityAcrossSection and the `entity` classification

The rule charged 15 points when a public method returned a type classified as an entity that lived
in another section — the "service boundary exists but leaks the domain model anyway" smell. It is
gone, along with the `entity` classification that existed only to feed it and the weight key that
priced it. `entity` had exactly one consumer, so the two retire as a unit.

Two reasons, and the second is the one that decides it:

1. **It scored zero on the corpus it was written for.** Humans' config pointed the `entity`
   classification at `src/Humans.Domain/Entities/**`, a project that does not exist, so nothing was
   ever tagged. (That misconfiguration is now reported — see the entry below — but a working
   diagnostic only makes the rule's silence legible; it does not make the rule useful.)
2. **It priced a constraint that belongs upstream of scoring.** The leak it charges for is
   impossible when entities are not public, which is a thing a codebase can simply enforce, and
   Humans does. A scoring rule that duplicates a hard block measures something that cannot happen.
   The adjacent idea — "nothing under `Contracts/` may be an entity" — is a reasonable rule, but it
   is an analyzer rule, not a score: it has a yes/no answer and wants to fail a build, not cost
   points.

`ScoreReturnTypeRules` now carries the canonical read-DTO credit alone and no longer needs the
type index. The `CampStayEntity` / `CampLegacyEntity` sample pair stays — it is the fixture proving
the canonical derivation reads *location and export*, never the name — but its comments no longer
describe a penalty that does not exist. Gate 1 backlog: 27 → 26.

A config still declaring an `entity` classification is now reported as
`unknown-config-classification` rather than quietly doing nothing.

## Unreleased - A config classification that matches nothing now says so

Two new warnings, `dead-config-classification` and `unknown-config-classification`, for the two
ways a `classifications` block can be inert.

The trap is the merge. `LoadOrDefault` merges defaults with `TryAdd`, so declaring a classification
**replaces** the built-in patterns for that key rather than extending them. A block that matches
nothing therefore does not fall back to the defaults — it switches its rules off. The rules keyed
to that tag then score zero, and zero is indistinguishable from a clean solution.

Found in Humans' config, which declares:

```json
"entity": { "paths": ["src/Humans.Domain/Entities/**"], "namespaces": ["Humans.Domain.Entities"] }
```

There is no `Humans.Domain` project — the solution is laid out as `src/Sections/Humans.<X>` plus
`.Contracts` assemblies, with no `Entities/` directory under `src/` at all. So nothing is tagged
`entity`, `methodReturnsEntityAcrossSection` cannot fire, and the report said nothing about it. The
default `*Entity` / `**/Entities/**` / `**/Models/**` patterns that would otherwise have matched
were replaced by the block, not merged with it.

Only classifications declared **in the config file** are checked. Defaults are speculative by
design — a solution with no controllers is not misconfigured — so warning about unmatched defaults
would put noise in every run, including this tool's own dogfood run. `SurfaceScoreConfig` records
which keys came from the file before the merge to tell the two apart.

The second warning covers the case the first cannot see: a key no rule reads. A typo'd `dtos` block
matches plenty of types and is still inert. The readable set is derived from the default key set
rather than hand-listed, so it cannot drift, and a test pins that the two are identical.

The `skill` command's config schema said classifications "override or extend" the built-ins. They
do not extend. Corrected, along with the omission of `entity` from the name list.

## Unreleased - Fix: diRegistration never fired

`diRegistration` has scored zero on every solution since it shipped. `ScoreDiRegistrationsAsync`
looked the registered service up by `typeInfo.ToDisplayString()`, but the dictionary it queries is
keyed by `SolutionClassifier.TypeKey` — `"{assembly}|{fully qualified name}"` — so the lookup could
never match. Every other consumer of that dictionary already built the key correctly; this was the
only one that did not.

Found by measuring Humans (`d237f3cc0`, reforge 0.28.1), which contains **452 generic
`AddScoped`/`AddSingleton`/`AddTransient` registrations, 341 of them interface-first**, and scored
0 points for the rule. Reforge's own dogfood run scored 0 too, but with one section and no DI
container that reads as normal — the bug needed a real corpus to be visible at all.

The rule now has a Gate 1 pair, which is the part that keeps it fixed.
`EveryDeclaredRule_ActuallyFiresInItsBeforeFixture` is exactly the assertion that catches "shipped
rule, fires nowhere", and the reason it did not catch this one is that `diRegistration` sat in
`NotYetCovered`. That list is therefore not just a gating backlog: every entry on it is a rule whose
behaviour is unverified in both directions.

The pair also records a second finding — `diRegistration` is **gameable**. Detection requires a
`GenericNameSyntax` with a type argument, so rewriting `AddScoped<IFoo, Foo>()` as
`AddScoped(typeof(IFoo), typeof(Foo))` registers the identical pair and charges nothing.

## Unreleased - Gate 1 tranche 2: the DTO rules, and a verdict that depends on an identifier

No product change. Four more rules covered, taking the backlog from 32 to 28. All four are
gameable, and three of them fail the same way.

**`booleanParameter` got two pairs, because it has two answers.** The cheapest fix is the same edit
either way — replace the bool with a two-value enum, which carries exactly the same choice at
exactly the same call sites. Name the enum `NotifyMode` and bind it to a parameter called `mode`
and `mutationModeParameter` charges 15 against the 3 saved: gated. Name it `NotificationPreference`
and bind it to `notifySubscribers` and nothing recognises it: the total falls. The backstop rule
matches enums by type suffix (`Action`, `Mode`, `Operation`, `Scope`, `Flags`, `Kind`) and by
parameter name, so which side of the gate the refactor lands on is decided by an identifier the
author picks *after* deciding to make the edit. One pair would have reported whichever half its
author happened to write, so the convention is now: when a verdict depends on something the fixture
author chooses, fixture both choices.

**`publicDtoType`, `dtoCollectionProperty`, `dtoNestedProperty`: all gameable, and the first is the
one that matters.** Classification is name-pattern-only, and it gates the *entire* durable-surface
pass — so dropping four characters from `GateShipmentDto` does not cost 5 points, it removes the
type from the score altogether along with every property charge on it. Every DTO rule shares one
failure mode and it is spelled with an identifier. `LooksLikeDataCarrier` already computes the
structural test (all public properties, no behaviour); it is applied after the name test rather
than instead of it.

The other two are the `dtoScalarProperty` finding again in different clothes: `List<string> Tags` →
`string TagsCsv` trades a 2-point charge for a 1-point one, and a nested DTO property → a JSON
string trades 3 for 1. In both, the same values cross the boundary with less type information, and
the nested case prices the strictly larger promise ("some JSON") below the smaller one. With
`dtoScalarProperty` from tranche 1, all four DTO rules now carry a finding — and they are not four
findings. They are one rule counting declarations where it means to count what crosses the
boundary.

## Unreleased - Gate 1 tranche 1, and somewhere to put a rule that fails

No product change. Test infrastructure and findings.

Four rules got fixtures. One holds; three do not, and the three are the point.

**A failing rule needed somewhere to go.** The gate was pass/fail, so the only way to record a rule
an agent can game was to leave the build red — and a red build is not a finding. It is a blocked
branch, and blocked branches get unblocked by tuning the fixture until it passes, which is the gate
quietly becoming the thing it was built to catch. A `Before` file may now carry
`// gate1-gameable: <why the cheapest fix is degenerate>`, and the assertion inverts: the score drop
becomes *required*. Repair the rule and the test fails, telling you to delete the marker — the one
moment a repair could otherwise pass unnoticed and leave a finding standing that is no longer true.

The marker asserts what no test can check: that the cheapest fix leaves the design no better. Some
rules want surface deleted, and for those the honest cheapest fix is a real improvement whose score
*should* fall. Marking that gameable would be a false accusation with a green check beside it, so
the note carries the argument and is required to be non-empty.

**Gated: `applicationServiceMethod`.** The cheapest way to make a per-method charge smaller is to
publish one door into all the operations and pick between them with an argument. Nothing is removed,
the boundary gets weaker, and `genericActionDispatcher`/`mutationModeParameter` charge more than the
two method charges saved. This is the case the gate was built around and it holds.

**Gameable: `dashboardAdminPageName`.** The rule matches the method's identifier, so the cheapest
edit is a rename — no return type, no parameters, no caller, no need to read the body. Rules keyed
on names can only ever cost an agent a rename. Fixing it is a design question (find screen coupling
structurally, or admit it is a naming lint and not a score), so it is recorded rather than patched.

**Gameable: `optionsBag`.** Unbundling the bag back into loose parameters satisfies the rule and
costs two `methodParameterOverflow` points against the eight it saves. A weighting failure, not a
detection failure: two rules describe roughly the same problem — too much crossing a boundary at
once — and price it 4× apart, which reads as an instruction to unbundle.

**Gameable: `dtoScalarProperty`.** Six typed properties collapse into one `Dictionary<string,
string>` for a net saving, and the rule cannot tell that apart from a real reduction. Every value
still crosses the boundary; what is gone is the type system's knowledge of them, which by any
reading is *more* durable surface, not less. Property count is a proxy for contract width and is
reducible without touching what it stands for. A second dodge is recorded in the fixture and not
separately fixtured: hoisting the properties to a base class whose name matches no DTO pattern also
zeroes the charge, because the rule reads `GetMembers()` and inherited members are not in it.

This also settles a question left open when the earlier `dtoScalarProperty` pair was withdrawn — the
nesting dodge really was not a fix, and the two suspected dodges recorded then are now one confirmed
finding and one confirmed scoping bug.

Buckets: 6 covered, 4 exempt, 32 pending.

## Unreleased - Gate 1 measures each variant instead of reconstructing it

No product change. Test infrastructure, closing #26.

The Gate 1 harness scored the sample solution once and reconstructed each fixture variant's total by
filtering the report to that variant's file. That is exact for rules which charge a declaration for
what the declaration says, and wrong for every rule which charges a *section* for its shape — the
`missing*` rules were recorded with no file at all and vanished from the comparison, while
`readSurfaceProjectionMethod` and `crossSectionWriteSurface` carried a real file and so looked like
ordinary per-declaration points despite being a function of which fixtures happened to coexist. A
fixture declaring a conventionally-named `…Info` type could become the section's primary DTO and
start charging projection points against *other* fixtures' files. Three findings, one mistake: a
variant's score was being derived from a shared compilation rather than measured.

Each variant is now compiled as a solution of its own (`IsolatedVariantScorer`, an `AdhocWorkspace`
over the fixture file plus the host's reference set), and the report's total *is* the variant's
score. Nothing to filter, nothing to attribute, no way for one fixture to move another's number.
Section identity is unchanged — a lone assembly named `SampleSolution.Gate` still folds to section
`Gate` — so config policy resolves exactly as it did and isolation changes only what the harness can
see, not what the engine decides.

Three consequences:

- The two guards that made the old failure modes loud are **deleted**. They were scaffolding around
  a defect; the defect is gone.
- The five section-coupled rules exempted as ungateable move back to not-yet-covered. Their
  exemption said "needs the isolated-variant harness", and that reason is now false. A variant
  compiled alone is a section, so these measure correctly — they are merely unwritten, which is a
  different claim. Buckets: 2 covered, 4 exempt, 36 pending.
- A variant that does not compile is now a **test failure** rather than a fixture scoring near zero
  and passing the gate by being empty. The shared harness could not tell those apart, because it
  never compiled a variant on its own. This also enforces the self-containment rule that was
  previously left to authoring discipline.

Isolation created one problem of its own, which is fixed here rather than exempted around. Sections
are derived from assembly names, so a variant compiled as a single project has exactly one section
and a cross-section rule cannot fire in it at all — not because the rule is sound but because the
harness cannot build the situation. Five rules (`crossSectionRepository`, `crossSectionReadInterface`,
`crossSectionFullService`, `crossSectionWriteSurface`, `methodReturnsEntityAcrossSection`) would have
sat in the backlog with no path out of it, which makes the backlog uncompletable by construction and
is the same shape of mistake as reconstructing a score: a limit of the measuring apparatus recorded
as a fact about the thing measured.

A variant may therefore carry satellite files — `<label>.<variant>.<Section>.cs` beside it — each
compiled as `SampleSolution.<Section>` and referenced by the primary project. The reference is
one-way, so which side is the consumer (the side a cross-section rule charges) is never ambiguous.
`IsolatedVariantScorerTests` proves the mechanism against a probe pair rather than a fixture:
demonstrating that the harness *can* fire a rule is a different claim from demonstrating that the
rule survives its cheapest fix, and only the second one earns a rule the "covered" label.

## Unreleased - Gate 1 is executable

No product change — no version bump, same as the CI change that preceded it. What changed is a
policy that was previously a promise.

The scoring spec says every scored rule must clear an anti-gaming gate: a `Before` that fires the
rule, a `CheapestFix` that is the laziest edit stopping it firing, and the requirement that **the
score not improve between them**. A rule whose cheapest fix lowers the score is worse than no rule,
because it spends an agent's effort and then reports the result as progress. Until now that was a
review promise, which is why `longMethod` — a rule the spec is now retiring — shipped despite
failing it.

`test/SampleSolution/SampleSolution.Gate/` carries the fixtures, one pair per label, discovered from
disk rather than listed in the test. The rules a pair targets are declared in a `// gate1:` comment
in the Before file, and the harness checks each named rule actually fires there, so a fixture that
gates nothing fails as a broken fixture rather than passing on two zeros.

The gate asserts two things per pair, not one. First, the declared rule must charge **strictly less**
in the cheapest fix — that is what makes the fixture a fix at all, since an agent edits code because
a number went down. Then, and only then, does "the total did not drop" mean anything. With the total
comparison alone, an unchanged copy of the Before file passes and the rule is recorded as gated
forever.

Two pairs so far:

- **`methodParameterOverflow`** — cheapest fix is the parameter object. Six values still cross the
  boundary; `parameterBagInput` and `optionsBag` charge for what the signature stopped declaring.
- **`tupleReturn`** — cheapest fix is a named result type. Better code, but not free: a published
  DTO is more durable surface than a tuple, since renaming a tuple element breaks nobody.

A third pair, for `dtoScalarProperty` and `publicDtoType`, was written and then withdrawn — the
first assertion above is what caught it. Pushing five of six properties into a nested DTO drops the
*parent type's* property count, but points are attributed by declaring file, so `dtoScalarProperty`
still charges six and the number an agent watches never moved. It was a relocation dressed as a fix.
Two edits that would genuinely reduce the charge — collapsing the scalars into one collection
property, or hoisting them to an unclassified base class, which `GetMembers()` does not see — are
suspected to lower the total as well, which would make the gate red. Both are unverified, and a
suspected-red gate is not something to ship on a hunch, so both rules moved to not-yet-covered.

The harness scores the solution once and reconstructs each variant's total by filtering to its file,
which is exact for per-declaration rules and wrong for section-coupled ones. Two shapes, both now
caught: the `missing*` rules are recorded against the section with an empty file and vanish from the
comparison, so a fixture declaring a repository would make the section repo-backed and switch them
on for every pair at once; `readSurfaceProjectionMethod` and `crossSectionWriteSurface` do carry a
file and so look like ordinary per-declaration points, but a fixture whose type name gets adopted as
the section's primary DTO causes them to be charged against *other* fixtures' files. Either way a
pair's score stops being a property of that pair. That now fails as a broken harness rather than
passing as a gate. Fixtures must also be self-contained — `oneImplementationInterface` and
`duplicateDbSetOwner` depend on what the rest of the solution declares — which is documented rather
than checked, because detecting it needs the compilation and not the report.

All of it is one defect: the variant's score is reconstructed from a shared compilation instead of
measured in isolation. Fixing that is #26, and it is what the five section-coupled exemptions point
at.

The other 40 rules are accounted for explicitly: 9 are exempt with a stated reason (one credit, five
section-coupled and so ungateable by this harness, three the spec retires), and 31 are listed as
not-yet-covered. A scored rule in none of the three buckets fails the build — so the
next rule added without a fixture is caught at the point where the fixture could still have been
written first. Bucket membership is checked in both directions and for overlap, so an entry that no
longer describes something true fails too — an exemption carries a reason, and a stale reason goes
on excusing every future rule of the same shape.

## v0.28.1 - split the scoring engine by pass

No behavior change. `SurfaceScoreEngine.cs` was 1,569 lines and the highest-churn file in the repo,
and every scoring item still on the roadmap adds a pass to it. Splitting after those land means
splitting a 2,500-line file instead.

The class is now `partial`, one file per pass, each named for what it charges for:

| File | What it scores |
|---|---|
| `SurfaceScoreEngine.cs` | the order the passes run in, and the accumulator they all write through |
| `ScoreReport.cs` | the report shapes every consumer reads |
| `.DurableSurface.cs` | pass 1 — what an assembly exports |
| `.DependencyUse.cs` | pass 2 — what a class reaches for through its constructor |
| `.SignatureShape.cs` | pass 3 — parameter overflow, bools, tuple returns, options bags |
| `.ReturnTypes.cs` | pass 4 — canonical read-DTO credit, cross-section entity leak |
| `.WriteCapableUse.cs` | pass 5 — write-capable interface used read-only |
| `.CrossCutting.cs` | duplicate DbSet owners, DI registrations, one-implementation interfaces |
| `.ImplementationShape.cs` | pass 6 — cognitive complexity, size, dispatcher/flags smells |
| `.SectionArchitecture.cs` | section rules, conservation anchors, helper candidates |
| `.BoundaryInputs.cs` | pass 7 — parameter/command objects on the boundary |

Every line moved verbatim; the split was done by extracting line ranges and verifying that the
multiset of lines across the new files differs from the original only by the added file headers.
No rule, weight, or threshold was touched, so scores are byte-identical to v0.28.0.

Deliberately **not** done: an `IScoreRule` abstraction. The passes do not have a common shape — some
walk classified types, some walk the whole solution's syntax trees, one consumes a pre-computed
section architecture — and inventing an interface they all fit would cost more than the file split
buys. This is a file split, nothing more.

One user-visible consequence, from v0.28.0's build-identity gate: the version change means a hot
server left running on v0.28.0 is no longer a dispatch target for a v0.28.1 client. It prints one
line and takes the cold path, as designed.

## v0.28.0 - the relay carries arguments, not a command line (protocol v2)

Two ways a hot server could answer differently from a cold run, both silent, both found by review
of v0.26.0 rather than by anything failing.

### Arguments are length-prefixed, not joined into a command line

The client used to join `args` into a shell-like string that the server re-split on spaces and
quotes. That round trip was lossy, and every loss was silent:

| Sent | Server ran |
|---|---|
| `snapshot --append 'daily"run.csv'` | `snapshot --append dailyrun.csv` — **wrote a different file, reported success** |
| `references 'say "hi"'` | `references say hi` — one argument became two |
| `references ''` | `references` — the argument vanished, shifting every positional after it |

The process already *has* the argument array; re-deriving it from text is what introduced the
ambiguity. The array now travels as an array, with each element's length in the header — the same
technique the response framing already used for stdout, and for the same reason. There is no
character with syntactic meaning left in the payload, so quotes, spaces, newlines, empty strings and
the framing's own header text all survive character for character.

This changes the request format, so **the protocol version is now 2** and the existing gate does the
rest: a v1 server and a v2 client decline to talk and the caller gets the cold path.

### A hot server must be the same *build*, not just the same protocol

The protocol version answers "can we read each other's bytes?" It says nothing about what the
commands inside those bytes do — and a command's contract can reverse while the envelope stays
identical. v0.27.0 is the case that proved it: `surface-score` refuses to score a degraded build and
exits 2, where v0.26.0 scored it and exited 0. Same protocol, opposite answers. A client that had
been upgraded while an older `reforge serve` was still running relayed into the old contract and
reported the old answer, with nothing to indicate the gate it had just installed hadn't run.

`.reforge-port` now carries a `build=` marker — the package version plus the module identifier — and
the client relays only to a server whose build matches its own exactly. The module identifier is not
decoration: the version does not change between rebuilds from source, which is precisely the
situation where a server is most likely to be stale. Any mismatch prints one line naming both sides
and takes the cold path, so the answer is always this build's answer.

The general rule this encodes: **a hot server is a cache of a build.** Anything that would make a
command behave differently makes the cached process wrong, and the port file is where that is
detected — before a byte is sent, because neither side can discover it in-band.

### Also

- The server reads a request to end-of-stream rather than a fixed three lines, since the payload may
  now contain newlines. That read is bounded (30s) because the accept loop is sequential — a client
  that connects and stalls would otherwise hold up every later request. The bound covers reading the
  request bytes only, never the command's execution.
- `SplitCommandLine` and `BuildCommandLine` are deleted rather than fixed. An escaping scheme is one
  more thing to get right on both sides; not needing one is better than getting one right.

Tests pin the arguments that used to be lost — quotes, quotes-with-spaces, empty, whitespace-only,
newlines, multibyte, and a value that is itself the request header — plus malformed frames in both
directions, and that a build differing only in module identifier is not a dispatch target.

This also retires v0.27.0's "restart your server after upgrading" instruction. The client now
detects a stale server itself, so nothing depends on the operator remembering.

## v0.27.0 - refuse to score a degraded build (BREAKING behavior change)

Reforge already knew when the solution had not compiled — `BuildInspector` counts error
diagnostics and retains them with file and line — and then scored anyway: a warning to stderr, an
entry in the JSON `diagnostics` array, **exit 0**, and a full score on stdout that reads as
authoritative. An agent running `surface-score --format json > out.json` never sees stderr and has
no reason to read one entry inside a twenty-key document.

That has put wrong numbers in a CHANGELOG, a design spec and a PR body. What made them convincing
is that two runs against the same broken tree **agree with each other**: during v0.25.0's A/B, a
tree carrying 3,723 compilation errors and 2,024 unresolved references matched the clean run on
`typesAnalyzed` and `internalComplexityTotal` while reporting `canonicalReadDtoReturn` at -81
against the clean tree's -162. Corpus agreement is not evidence of soundness. `build.degraded` is
the only reliable check and nothing forced anyone to make it.

- **`surface-score` now prints nothing and exits 2 when the build is degraded.** Suppressing stdout
  is the part that does the work — a number that is never printed cannot be pasted into a
  changelog. Exit **2**, not 1, so "the tree was broken" is machine-distinguishable from "the tool
  failed".
- **`--allow-degraded` is a real opt-out and exits 0.** A flag that still fails is a flag people
  route around with `|| true`, which suppresses genuine failures too. With it the score is printed,
  the `degraded-build` diagnostic is present in every format exactly as before, and the exit code
  is 0.
- **stderr names the counts and the individual errors** — `CSxxxx  <path>:<line>  <message>
  (<project>)`, the same shape Compact and Markdown already use — followed by a pointer to
  `--allow-degraded`. Capped by `--max-build-diagnostics` (default 25); the counts are never capped.
  The diagnosis names the command you actually ran; the shared build description used to open with
  "Surface-score is PARTIAL" regardless, which read as a bug in the tool when you had run
  `section-shape`. The string embedded in `surface-score`'s JSON `diagnostics` array is unchanged.
- **`section-shape` is covered by the same contract** and gains `--allow-degraded` and
  `--max-build-diagnostics`. It never inspected build health at all, though its anchors and
  `missing*` findings come off the same semantic model and break the same way. It inspects *before*
  running the section analysis, so a broken tree skips that work rather than computing output
  nobody will print.
- **`--list-groups` is refused too.** A section list read off a broken compilation misleads the
  same way a score does, and one contract is easier to rely on than a per-flag exception.
- Both commands are the first in the tool to return an exit code from their action rather than
  always yielding 0.
- Clean builds are unaffected: same output, byte for byte, and exit 0.

**Consumer impact.** Humans' `pr-surface-report.py` parses this JSON and will now see exit 2 with
empty stdout when the tree is broken. That is the point, but it needs a matching update rather
than being surprised by it.

**Hot mode.** The contract holds there because of v0.26.0, which is what makes these two commands
run on the server at all and what carries the exit code and stderr back through the response frame.

**Restart your server after upgrading to this version:** `reforge stop && reforge serve`. A `serve`
process left running from v0.26.0 advertises the same protocol version this build does — the wire
format did not change, only the command's behavior did — so the client cannot tell them apart and
relays into the old code, which scores the degraded build and exits 0 exactly as it always did. The
gate applies to cold runs regardless; it is only relayed runs that reach the stale process. (A
server older than v0.26.0 was never affected: it advertises no protocol version at all, so the
client already declines to relay to it.) v0.28.0 removes the need to remember this by putting the
build's identity in the port file and refusing to relay to any other build.

## v0.26.0 - one command registry (fixes the hot-mode silent failure)

With a hot server running, `reforge surface-score`, `snapshot`, `cycles` and `section-shape`
printed the root help text and **exited 0**. Four of the tool's commands — including the #3 and #4
most-used, 1,331 recorded runs between them — silently did nothing whenever the fast path they
exist to use was available. An agent reads exit 0 with empty output as "ran fine, no findings".

The cause was four hand-maintained lists of the command surface that disagreed: `Program` listed
29, `ServeCommand` 21, the agent-facing skill doc 24, the README 14. `ServeCommand`'s copy was last
updated 2026-04-13, so every command added after that date was registered on the cold path and
nowhere else. Nothing failed loudly because `TryRelayAsync` reported success for any completed
socket round-trip and the server redirects stderr to `TextWriter.Null`.

- **New `CommandRegistry` is the single list.** `Program` (cold) and `ServeCommand` (hot) both
  build their root command from it, so a command cannot exist on one host and not the other.
  Adding a command is one entry plus one factory arm; a spec with no factory throws by name at
  startup on both hosts rather than going missing at runtime.
- **Relay eligibility is a registry property**, not a string list in `Program`. The five plumbing
  commands are ineligible for stated reasons (`serve` would nest a server, `stop` would kill the
  one in use, `install` and `request` write outside the repo, `skill` prints a static document),
  and the server no longer registers them. Note `skill` *was* registered server-side and could
  never be reached, the same drift in the other direction.
- **An unknown command is no longer relayed.** It reaches the cold path so `System.CommandLine` can
  report an unrecognized command with a non-zero exit. Previously any first argument was forwarded,
  and a typo came back as help text and exit 0.
- **Relay eligibility is decided from the whole argument list**, not `args[0]`. The root's
  `--solution`/`--format`/`--limit` are recursive, so `reforge --format json cycles` is valid and
  its first argument is an option; deciding on `args[0]` would have pushed every such invocation
  onto the cold path while a server sat idle.

### The relay protocol is versioned (v1)

Widening what gets relayed exposed how thin the wire format was: it could carry bytes but not a
result. Fixing that piecemeal would have left a client and server from different commits talking
past each other, so the format is now versioned, and **the version handshake happens out of band in
`.reforge-port`, before a byte is sent** — neither side can negotiate in-band, because the thing
they disagree about *is* the format.

- **Exit codes.** `TryRelayAsync` returns `int?`: the command's exit code, or `null` when the
  caller should cold-start. "The server ran this and it failed" and "there is no server" were
  previously the same `false`.
- **stderr is carried separately** instead of being redirected to `TextWriter.Null`. Some commands
  report only there — `section-shape --section <unknown>` names the bad filter on stderr and then
  prints a legitimate-looking empty result on stdout — so a relayed run used to discard the one
  actionable message. The response is length-prefixed rather than delimited, so output containing
  the header text cannot forge a section boundary and stdout survives character for character.
- **The client's working directory travels with the request.** The server runs in whatever
  directory it was started from, so a relative `--config`, `--baseline` or `snapshot --append` path
  used to resolve against the server's directory rather than the caller's — newly reachable now
  that those commands relay, and `snapshot --append` writes a file.
- **Version mismatch degrades to the cold path in both directions.** A pre-v1 client cannot parse a
  v1 port file (the protocol marker is on a second line, so its `int.TryParse` of the whole file
  fails) and skips the relay rather than printing the frame as output. A v1 client finding no
  marker says so on stderr and cold-starts rather than relaying into a server whose command table
  predates the commands it would send. Both are slower and correct.
- **No cold-path retry after dispatch.** Once a request is on the wire the server may have executed
  it, so a later transport failure reports the error instead of re-running the command locally —
  `snapshot --append` would otherwise write its CSV row twice.
- **The server answers even when handling throws.** A failure used to leave the client reading an
  empty response, indistinguishable from a clean run with no output; it now returns exit 1 and the
  message.
- `reforge stop` still sends a bare `__shutdown__` line, understood by every server version — the
  advertised fix for a version mismatch must not itself require a matching build.

Tests: `CommandRegistryTests` pins that every spec resolves to a factory whose command name matches,
that the two hosts differ by exactly the five plumbing commands, that the four previously-missed
commands are served, that `skill` is not, that the command token is found behind global options,
and that ineligible/unknown/empty/unframed requests are refused with a non-zero exit.
`ServerProtocolTests` pins the framing both ways: exit-code round trip, stream separation,
character-exact stdout, hostile output that mimics the header, multibyte text, and both directions
of the port-file version gate.

Scoring output and every command's behavior on the cold path are unchanged.

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

Measured on Humans against a **clean** build — `build.degraded` false and zero unresolved references
on both sides, `typesAnalyzed` 2,836 and `internalComplexityTotal` 3,127 on both sides, 46 sections:
`surfaceTotal` 17,033 -> 16,886 (-0.9%), 24 of 45 scoring sections unchanged. Three rules moved, and
the movement is the finding:

- `canonicalReadDtoReturn` -3 -> -162. Nine config blocks listed 34 DTO names between them and
  granted the credit exactly once. Derivation credits eight sections, led by Application (-84),
  Infrastructure (-24) and GoogleIntegration (-21) — none of which had a config block at all.
- `missingPrimaryInfoDto` 230 -> 110. Thirteen sections resolve a primary anchor through the derived
  set that the `<Section>Info` convention missed. Calendar moves the other way, 0 -> 10: its
  configured canonical DTO `CalendarEventInfo` is an `internal sealed record` under `Services/Dtos/`,
  so Calendar publishes no read API and the config had been asserting one no consumer can reach.
- `readSurfaceProjectionMethod` 116 -> 248. The projection surcharge only fires for a section with a
  resolved primary anchor — without it a primitive read can't be told from a projection. The newly
  anchored sections therefore reveal projection debt that had been invisible (Governance +40,
  Expenses +32).

A later re-measurement caught the Humans tree mid-breakage (3,723 compilation errors, 2,024
unresolved references) and produced materially different deltas — `canonicalReadDtoReturn` -81
rather than -162. Same change, same command, wrong tree. Only figures from a run with
`build.degraded` false are quoted here.

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
