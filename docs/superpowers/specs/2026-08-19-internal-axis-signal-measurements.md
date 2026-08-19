# Internal-axis candidate signals — Gate 2 measurements

## Status

Measurement record (2026-08-19). Discharges the Gate 2 obligation in
`2026-08-15-scoring-alignment-design.md` for the two candidate signals issue #19 proposes, and
answers the two open questions issue #35 flags for the `publicWriteSurface` transform.

**No rule is proposed for immediate implementation.** All three signals measured are recommended
*against* in the shape they were proposed — two on precision, one because it cannot be calibrated
from this corpus. One deletion *is* recommended, and it is free: `crossSectionWriteSurface` scores
0/44, so retiring it and its suppression machinery is a measured no-op.

That is the point of the gate. A measurement round whose output is "not yet, and here is what would
settle it" is the gate working, not the gate failing.

Every number here is measured. Nothing is extrapolated.

## Method

A throwaway Roslyn harness (~300 lines, not committed — same treatment as the type-2 clone detector
whose negative result the 2026-08-15 spec records) opened `Humans.slnx` and walked every non-test
project, excluding `obj/`, `Migrations/`, `*.g.cs` and `*.Designer.cs`.

**Exact inputs**, so these numbers can be re-derived rather than taken on trust:

| | |
|---|---|
| Humans | `113061bcf5f6`, committed 2026-08-19 |
| reforge | `50ebc39faa87` (version 0.28.1, `main` after #48) |
| SDK | .NET 10.0.111 |
| corpus | 44 sections, 3,448 types, 157,860 prod LOC, 5,523 method bodies |
| score | `surfaceTotal` 17,379, `internalComplexityTotal` 3,113, `degraded: false` |

The harness is not committed, so the **signal definitions are stated as predicates** below rather
than left implicit in code that no longer exists. Each is a few lines of Roslyn over the same walk:

- **Single-reference private helper.** A method declaration whose symbol is `private`, has a body,
  and whose `OriginalDefinition` is the resolved symbol of exactly one `SimpleNameSyntax` node in its
  declaring assembly (excluding the declaration's own name). Counting name nodes rather than
  invocations is what makes a method group — a delegate assignment, an event subscription — count as
  a reference; counting per assembly is exact for `private`, which nothing outside can reach.
- **Feature envy.** A method with at least one parameter, whose first parameter's type is a
  solution-declared class or struct other than the containing type. `targetTouches` counts
  `MemberAccessExpressionSyntax` nodes whose **receiver resolves to that first parameter**;
  `selfTouches` counts explicit `this.X` accesses plus unqualified names resolving to an instance
  member of the containing type (implicit `this`). Fires when
  `targetTouches >= 3 && targetTouches > selfTouches * 2`. Binding to the receiver rather than to the
  member's declaring type is load-bearing — see the correction recorded under Signal B.
  - *mapper* — the method's return type (unwrapped one level through `Task<T>` / `ValueTask<T>` /
    a single-type-argument generic) differs from the parameter's type, and the body contains an
    `ObjectCreationExpressionSyntax` or `ImplicitObjectCreationExpressionSyntax` of that return type.
  - *scalar result* — return type (same unwrapping) is an enum or one of `bool`, `string`, `int`,
    `long`, `decimal`, `double`, `float`, `DateTime`.
  - *synchronous* — return type is not `Task` / `ValueTask`.
- **Write surface.** Distinct declaring files carrying a `fullServiceInterfaceMethod` entry in
  `surface-score --format json --all`, grouped by section. No harness needed for this one; it is
  readable off the report.

## Context reproduced

Before measuring anything new, the finding the whole rework rests on, re-taken at a newer commit
with a newer reforge:

| | `longMethod` | `cognitiveComplexity` | `largeClass` | size total | design total |
|---|---:|---:|---:|---:|---:|
| #19, `14a2760`, reforge 0.25.0 | 1,468 | 837 | 435 | 2,740 (88%) | 386 (12%) |
| here, `main`, reforge 0.28.1+ | 1,490 | 771 | 445 | 2,706 (87%) | 426 (13%) |

**87% of the internal axis is still satisfiable by extract-method.** The finding holds; it is not an
artifact of one commit or one tool version.

Two other standing figures moved and are worth recording:

- The six read-surface rules #19 proposes retiring now total **2,047 points, 11.8% of surface** — up
  from the 1,230 / 7% #19 measured. The share **grew**. If the argument for cutting them was
  value-per-line, that argument is now stronger, not weaker.
- `crossSectionWriteSurface` still scores **0 across all 44 sections**, confirming #35's reading on a
  second, larger corpus.

---

## Signal A — single-caller non-public helper

**Proposed by #19 as:** "Single-caller private helper (net-new). Directly measures the
extract-to-satisfy-the-linter artifact. Needed as a counterweight if any size rule is retained."

### Distribution

1,319 **private** methods with bodies, by number of references in their declaring assembly:

| refs | methods | share | median LOC |
|---:|---:|---:|---:|
| 0 | 31 | 2.4% | 5 |
| **1** | **640** | **48.5%** | 16 |
| 2 | 372 | 28.2% | 12 |
| 3 | 116 | 8.8% | 9 |
| 4 | 56 | 4.2% | 7 |
| 5+ | 104 | 7.9% | 7 |

282 of the 640 single-reference helpers are under 15 LOC. Highest counts: Analyzers 66, Users 66,
Shifts 60, GoogleIntegration 50, Web 40.

**Two corrections from review, both of which this table already reflects.** The first pass measured
the wrong population and undercounted references, and both errors pushed methods *into* the
one-reference bucket:

- It included `internal` and `protected internal` while counting only within the declaring assembly.
  Neither is assembly-private in practice — `internal` is reachable from friend assemblies through
  `InternalsVisibleTo` (the test projects this walk excludes), `protected internal` by deriving from
  another assembly — so their callers could not be counted from here at all. #19 proposes a
  **private** helper rule; that is now the population.
- It counted invocations plus argument-position identifiers, which misses a method group used as a
  delegate or event handler (`x.Changed += Bar`, `Foo = Bar`). Counting *name nodes* catches call
  sites and method groups alike, once each.

The corrected figure is **48.5%** against the first pass's 48.9% — the flaws were real but the
aggregate barely moved, which is itself worth recording: the conclusion did not depend on them.

### Reading

**The base rate is 48.5%.** Nearly half of all private methods in this codebase have exactly one
reference — in a corpus nobody has accused of extract-method fragmentation, and much of which
predates any LLM involvement.

A rule charging per standing single-reference helper would therefore charge roughly half of all
private methods in any codebase. That is not a smell detector; it is a tax on decomposition, and it points
an agent at *inlining private methods back into their callers* — which is the opposite of the design
sense the axis is supposed to reward. A single-caller private helper with a good name is one of the
cheapest legitimate things in programming.

### Decision

**Reject as a stock signal. Confirmed as a delta signal**, which is what the 2026-08-15 spec already
says ("meaningless as a stock — it only exists as a delta"). This measurement supplies the evidence
for *why*, which the spec did not have: the stock is not merely noisy, it is half the population.

Consequence for implementation: the rule cannot be computed from one report. It needs the baseline
comparison in `SurfaceScoreBaseline` to diff helper sets between two runs and charge only helpers
that are **new in this change** and have exactly one caller. That is a delta-native rule, and the
only one proposed so far — worth noting because nothing in the current engine is shaped that way.

---

## Signal B — feature envy

**Proposed by #19 as:** "For each entity or DTO, count external methods whose primary parameter is
that type and which touch only that type's members — methods that belong *on* the type. This is the
measurable 'encourage OO' signal, and it is not extraction-gameable: the only cheap fix is moving the
method, which is the desired outcome."

The 2026-08-15 spec independently lists "moving a method onto the type whose data it uses" as a
candidate for a second **credit**, which makes precision matter more than usual: a credit that fires
on the wrong shape pays an agent to do the wrong thing.

### As specified

Criteria: primary parameter is a solution-declared class or struct other than the containing type;
the body touches its members at least 3 times; and at least twice as often as the containing type's.

**360 candidates.**

Manual read of the top 15 — the gate's requirement — finds the population dominated by one shape:

| method | envied type |
|---|---|
| `ShiftDashboardPageBuilder.BuildAsync` | `ShiftDashboardPageRequest` |
| `CampController.MapToEditViewModel` | `CampEditData` |
| `TeamService.BuildTeamInfo` | `Team` |
| `CampService.CreateCampSeasonInfo` | `CampSeason` |
| `EventSettingsFormMapper.Parse` | `EventSettingsViewModel` |
| `ExpenseReportMapper.ToDto` | `ExpenseReport` |
| `IssuesApiController.MapDetailIssue` | `IssueDetail` |
| `BudgetService.ToGroupDetail` | `BudgetGroup` |

These are **mappers**: they read one type and build a different one. Moving a mapper onto the type it
reads is not the desired outcome — it is a **dependency inversion**. `ExpenseReport` would have to
reference `ExpenseReportDto`; a domain entity would depend on its own projection. The refactor the
rule implies is actively wrong for this shape, and this shape is most of the population.

### Refinement 1 — exclude structurally-detectable mappers

Excluding methods that construct their own return type from the parameter:

- **170 of 360 (47.2%) are mappers** by that structural test.
- 190 remain.

But a manual read of the *non-mapper* top 15 still finds `MapDetailIssue`, `MapList`,
`MapTeamSummary`, `CloneUserInfoFields` and `EventSettingsFormMapper.Apply` — at least 5 of 15 are
mappers the structural test missed, because they build collections, mutate an existing instance, or
construct through a helper. **True precision after this filter is well under 50%.**

### A correction that mattered here more than in Signal A

The first pass counted a "touch" as any member access whose *declaring type* was the parameter's
type, regardless of the receiver — so a second `T`-valued local, field or static counted toward the
numerator. And it counted `selfTouches` only from `MemberAccessExpressionSyntax`, missing implicit
`this` access entirely, which is how most self-access is actually written. Both biases point the
same way: toward firing.

Touches are now bound to the receiver (the member access must be *on the first parameter*), and
implicit-`this` names count toward `selfTouches`. Raw candidates fell 385 → 360 and the mapper share
barely moved, but **membership changed at the margins** — the case I had held up as the textbook hit,
`MailerAdminController.DriftedMoreThanTenPercent`, drops out under receiver binding. Its 18 touches
were not all on its parameter.

That is the useful lesson: the aggregate was robust to the flaw, the individual findings were not.
Anyone weighting this rule would have been weighting the aggregate, but anyone *acting* on it reads
the list.

### Refinement 2 — non-mapper, scalar result, synchronous

Two further conditions, each with a stated reason:

- **Returns a primitive, enum or string.** The method answers a question *about* the parameter rather
  than building something *from* it. This is the shape where "move it onto the type" is
  unambiguously right — a predicate or a computed fact belongs on the data it reads. Anything
  returning a constructed type is a projection.
- **Synchronous.** The remaining false positives after the scalar filter were all `*Async`
  persistence and IO — `UpdateLineItemAsync`, `SyncFolderBasedDocumentAsync`, `UpsertContactAsync`.
  Moving those onto the data type would put IO on a DTO.

**360 → 190 → 38 → 26 candidates.**

Manual read of all 26:

| genuine feature envy (19) | presentation / formatting (6) |
|---|---|
| `ProfileCompletion.ComputePercent(ProfileInfo)` / `(Profile)` | `AgentPromptAssembler.BuildUserContextTail(AgentUserSnapshot)` |
| `GateAdmissionRules.Evaluate(GateScanContext)` | `AgentPromptAssembler.RenderShiftEntry(UpcomingShiftEntry)` |
| `FeedbackService.NeedsReply(FeedbackReport)` | `ProfileApiController.FormatContactFieldDetail(ContactFieldDto)` |
| `ShiftManagementService.CalculateScore(Shift)` | `PersonSearchMatcher.DisplayLabel(ContactFieldInfo)` |
| `GoogleWorkspaceSyncService.IsDirectManagedPermission(DrivePermission)` | `TeamAdminController.BuildResourceLinkError(LinkResourceResult)` |
| `GoogleWorkspaceSyncService.IsAnyUserPermission(DrivePermission)` | `SurveyCsvExportBuilder.QuestionHeader(SurveyExportQuestion)` |
| `SurveyBranchingEvaluator.IsVisible(BranchCondition)` / `Matches(BranchClause)` | |
| `CantinaRosterAssembler.HasAnyAllergyOrIntolerance(RosterPersonDto)` | |
| `EarlyEntryCapacityCalculator.GetAvailableEeSlots(EventSettings)` | |
| `UserStateClassifier.Classify(User)` | |
| `MailerImportService.ClassifyVerifiedMatch(MailerLiteSubscriber)` | |
| `SurveyController.IsAnswerable(SurveyDetail)` | |
| `ExpenseReportService.ResolveSyncState(HoldedExpenseOutboxEvent)` | |
| `Service.ClassifyStripeSession(StoreCheckoutSessionData)` | |
| `GateScanCardViewModel.TooEarlyReason(GateScanResult)` | |
| `UserService.HasRequiredNameFields(Profile)` | |
| `CalendarOccurrenceViewExtensions.ShouldHideTimeLabel(CalendarOccurrence)` | |

**≈73% precision at 26 hits across 44 sections.** `SurveyBranchingEvaluator.IsVisible(BranchCondition)`
and `UserStateClassifier.Classify(User)` are the shape the rule was proposed for: a predicate or
classification about a data type, living somewhere else.

Two things about the residue:

- The false positives are a coherent, nameable class — `Render*` / `Format*` / `Display*` /
  `Build*Label`. Whether they are false positives at all is arguable: a display name for a `TeamInfo`
  is a fact about a `TeamInfo`. But pushing view concerns onto a DTO is a trade many teams refuse, so
  they should not be charged without the owner taking that position first.
- **One of the 26 is already an extension method** (`ShouldHideTimeLabel`). An extension method is
  C#'s idiom for "this belongs on the type and cannot be put there", i.e. the fix already applied as
  far as the language allows. A scored rule should exclude them, or it charges people for having
  complied. One instance here, but it is a definitional exclusion rather than a tuning knob.

### Decision

**Reject as specified in #19. Recommend the refined form for a follow-up measurement round, not yet
for a weight.**

Concretely:

- The rule as written in #19 would spend most of its points telling an agent to invert a dependency.
  This is the same class of result as the rejected duplication rule: a plausible signal whose top
  hits are the wrong thing.
- The refined form (non-mapper + scalar/bool/enum/string return + synchronous) is a usable signal at
  ~70% precision and a tractable 26 hits.
- Before it earns a weight it needs: a decision on the `Render*`/`Format*` class, and a re-measure
  against a second corpus, because 26 hits on one codebase is a thin basis for a weight.
- If it lands as a **credit** rather than a penalty, precision matters more, not less — a credit
  firing on a mapper pays for a dependency inversion. The refined form is the only version safe to
  consider as a credit.

---

## Signal C — `publicWriteSurface` (issue #35)

#35 proposes retiring `crossSectionWriteSurface` — a three-condition conjunction that measures
**0/44 sections** — and replacing it with a declaration-side rule that charges for *exporting* write
capability at all. It flags two things to decide. Both are measurable, so both are measured.

### Population

| | count |
|---|---:|
| write-capable service interfaces (exported) | **47** |
| sections declaring at least one | **24 of 44** |
| methods on them | 517 |
| already charged by `fullServiceInterfaceMethod` (8/method) | **4,136 points** |

Mean 11 methods per write interface. But the mean hides the shape, and the shape is the finding:

| write interfaces in the section | sections |
|---:|---:|
| 1 | **19** |
| 2 | 2 |
| 3 | 1 |
| 5 | 1 |
| **16** | **1** (Users) |

**19 of the 24 sections that have a write interface have exactly one.** So the distribution is
effectively binary plus a single outlier: 20 sections at zero, 19 at one, four in between, and Users
at sixteen.

That shape forces a choice #35 does not make explicitly, and the two readings do not price the same
thing:

- **Per section (binary)** — "this section publishes write capability". Fires on 24 of 44, adds the
  same amount to each, and separates 24 from 20. This is the reading that matches #35's rationale
  that a section "has crossed the line once", and it is genuinely orthogonal to
  `fullServiceInterfaceMethod`, which prices width.
- **Per interface** — "this section publishes *N* write APIs". On this corpus that is the binary
  signal for 43 of 44 sections and a 16× charge for Users. It also contradicts the crossed-the-line
  rationale, since it charges Users sixteen times for one decision — and it measures the same
  dimension `fullServiceInterfaceMethod` already measures more finely, which is exactly the
  "weight change wearing a new name" #35 worries about.

**The per-section reading is the defensible one**, and it is not what a naive implementation of
"charge for exporting write capability" would produce. Worth settling before the rule is written,
not after.

Either way, the calibration problem stands: past the binary split, all remaining discriminating
power is one observation — Users declares sixteen write interfaces, three times the next section.
That is a real and interesting finding, and it is **n = 1**.

### Question 1 — does it double-charge `fullServiceInterfaceMethod`?

**No, provided the charge is flat per interface.** The two price different decisions:

- `fullServiceInterfaceMethod` scales with **width** — how much write API a section published.
- `publicWriteSurface` would price the **decision to publish a write API at all**, which is a
  separate architectural fact: a section with one write method and a section with thirty have both
  crossed the same line once.

The distinction is only real if the new rule is flat. A per-method or size-scaled charge would be a
weight change on `fullServiceInterfaceMethod` wearing a new name, exactly as #35 suspects.

Modelled cost of each reading:

| weight | per section (24 charges) | per interface (47 charges) | per-interface share landing on Users |
|---:|---:|---:|---:|
| 5 | 120 | 235 | 34% |
| 10 | 240 | 470 | 34% |
| 15 | 360 | 705 | 34% |
| 25 | 600 | 1,175 | 34% |

Under the per-interface reading a third of the rule's entire output lands on one section at every
weight. A weight of 25 — the current `crossSectionRepository` level — would additionally make this
the 6th largest rule in the system on its first day, off 47 declarations.

### Question 2 — what happens to `crossSectionSuppress`?

`ScoreAsync` maintains a suppression set purely so `crossSectionWriteSurface` can pre-empt
`writeCapableInterfaceUsedReadOnly` for the same caller/dependency pairs. Since the old rule fires
**0 times on 44 sections**, that set is empty in practice, and `writeCapableInterfaceUsedReadOnly`
scores 60 points across 5 files in 3 sections regardless.

**Retiring the old rule and its suppression set is a measured no-op on this corpus.** It is a pure
simplification, not a behaviour change — which is the cheapest kind of deletion to justify.

### Decision

Split, because the two halves of #35 measure very differently.

**Retire `crossSectionWriteSurface`: yes, and it is free.** It scores 0/44, and its suppression set
is empty in consequence. Deleting the rule, the set, and the branch in `ScoreAsync` is a measured
no-op — a pure simplification.

**Add a scored `publicWriteSurface`: not yet, and settle the shape first.** #35's double-charging
question has an answer, but it depends on the reading: a **per-section** charge prices a decision
`fullServiceInterfaceMethod` does not price at all, and the codebase already prices
`newRepositoryInterface` at 15 beside `repositoryInterfaceMethod` at 10, so the shape has precedent.
A **per-interface** charge measures the dimension `fullServiceInterfaceMethod` already measures, only
coarser — that one *is* a weight change wearing a new name, and it contradicts the crossed-the-line
rationale by charging one section sixteen times for one decision.

What cannot be answered from this corpus either way is the **weight**: past the binary split the
distribution is a single outlier, so any number picked here is fitted to Users.

That is precisely the failure Gate 2 exists to prevent, so the gate should hold.

**Recommended instead: report it, don't score it.** The 2026-08-15 spec already has a "Reported, not
scored" category for exactly this state — a signal believed real, not yet calibratable. Surfacing
the exported-write-interface count per section in the `metrics` block added by #45 costs nothing,
changes no total, and makes the distribution visible on every corpus reforge is ever run against.
Pick the weight when a second corpus shows whether 19-of-24-have-exactly-one is a property of
Humans or a property of sectioned codebases.

---

## Consequences for the 2026-08-15 spec

1. **Gate 2 is discharged for both #19 candidates.** One is confirmed delta-only with evidence; the
   other is rejected as specified and refined into a form worth one more round.
2. **The per-section size denominator that spec asks for now exists.** It lists under the cleanup
   loop: *"Per-section density, not absolute points, must be derivable from the report… `typesAnalyzed`
   exists; a per-section size denominator does not."* It does now — #45 added a `metrics` block per
   section carrying `locProd`, `files`, `classes`, `interfaces`, `methods` and both complexity
   distributions, plus a solution rollup.
3. **The read-surface retirement argument strengthened**, from 7% of surface to 11.8%.
4. **Nothing here changes a weight.** All three signals are recommended against in their proposed
   form: two on precision, the third because its distribution on this corpus is one constant and one
   outlier, so any weight would be fitted to a single section. Each has a stated next step; none of
   them is "pick a number now".

## What was not measured, and why

- **A second corpus.** Every number is from Humans. Reforge itself is 1 section and 107 types with no
  DTOs and no repositories, so it cannot exercise these signals — it is a plumbing check, not a
  model check, as the 2026-08-15 spec already records.
- **Net-new single-caller helpers.** Requires two runs across a real change; the stock measurement
  above is what one run can establish, and it was enough to settle the question asked.
- **Whether the refined feature-envy hits are worth fixing.** Precision is measured; value is not.
  26 findings that an owner would decline to act on would be 26 findings too many.
