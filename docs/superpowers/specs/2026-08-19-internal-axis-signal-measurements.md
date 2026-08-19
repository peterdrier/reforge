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
whose negative result is recorded in the 2026-08-15 spec) opened `Humans.slnx` and walked every
non-test project, excluding `obj/`, `Migrations/`, `*.g.cs` and `*.Designer.cs`.

**Corpus.** Humans at `main`, 44 sections, 3,448 types, 157,860 prod LOC, 5,523 method bodies.
`surfaceTotal` 17,379, `internalComplexityTotal` 3,113, degraded: false.

Reproducing it needs only the public `peterdrier/humans` checkout and a .NET 10 SDK; `reforge` opens
that solution cleanly, which is what makes any of this checkable rather than assertable.

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

1,444 non-public methods with bodies, by number of call sites in their declaring assembly:

| callers | methods | share | median LOC |
|---:|---:|---:|---:|
| 0 | 50 | 3.5% | 7 |
| **1** | **706** | **48.9%** | 16 |
| 2 | 402 | 27.8% | 12 |
| 3 | 117 | 8.1% | 9 |
| 4 | 56 | 3.9% | 7 |
| 5+ | 113 | 7.8% | 7 |

315 of the 706 single-caller helpers are under 15 LOC. Highest counts: Shifts 86, Users 67,
Analyzers 66, GoogleIntegration 55, Web 41.

### Reading

**The base rate is 48.9%.** Nearly half of all non-public methods in this codebase have exactly one
caller — in a corpus nobody has accused of extract-method fragmentation, and much of which predates
any LLM involvement.

A rule charging per standing single-caller helper would therefore charge roughly half of all private
methods in any codebase. That is not a smell detector; it is a tax on decomposition, and it points
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

**385 candidates.**

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

- **184 of 385 (47.8%) are mappers** by that structural test.
- 201 remain.

But a manual read of the *non-mapper* top 15 still finds `MapDetailIssue`, `MapList`,
`MapTeamSummary`, `CloneUserInfoFields` and `EventSettingsFormMapper.Apply` — at least 5 of 15 are
mappers the structural test missed, because they build collections, mutate an existing instance, or
construct through a helper. **True precision after this filter is well under 50%.**

### Refinement 2 — non-mapper, scalar result, synchronous

Two further conditions, each with a stated reason:

- **Returns a primitive, enum or string.** The method answers a question *about* the parameter rather
  than building something *from* it. This is the shape where "move it onto the type" is
  unambiguously right — a predicate or a computed fact belongs on the data it reads. Anything
  returning a constructed type is a projection.
- **Synchronous.** The remaining false positives after the scalar filter were all `*Async`
  persistence and IO — `UpdateLineItemAsync`, `SyncFolderBasedDocumentAsync`, `UpsertContactAsync`.
  Moving those onto the data type would put IO on a DTO.

**385 → 201 → 38 → 26 candidates.**

Manual read of all 26:

| genuine feature envy (≈19) | presentation / formatting (≈7) |
|---|---|
| `MailerAdminController.DriftedMoreThanTenPercent(ImportPlanCounts)` | `AgentPromptAssembler.BuildUserContextTail(AgentUserSnapshot)` |
| `ProfileCompletion.ComputePercent(ProfileInfo)` / `(Profile)` | `AgentPromptAssembler.RenderShiftEntry(UpcomingShiftEntry)` |
| `GateAdmissionRules.Evaluate(GateScanContext)` | `ProfileApiController.FormatContactFieldDetail(ContactFieldDto)` |
| `FeedbackService.NeedsReply(FeedbackReport)` | `ProfileCardViewComponent.GetDisplayName(TeamInfo)` |
| `ShiftManagementService.CalculateScore(Shift)` | `PersonSearchMatcher.DisplayLabel(ContactFieldInfo)` |
| `GoogleWorkspaceSyncService.IsDirectManagedPermission(DrivePermission)` | `TeamAdminController.BuildResourceLinkError(LinkResourceResult)` |
| `SurveyBranchingEvaluator.IsVisible(BranchCondition)` / `Matches(BranchClause)` | `SurveyCsvExportBuilder.QuestionHeader(SurveyExportQuestion)` |
| `CantinaRosterAssembler.HasAnyAllergyOrIntolerance(RosterPersonDto)` | |
| `EarlyEntryCapacityCalculator.GetAvailableEeSlots(EventSettings)` | |
| `UserStateClassifier.Classify(User)` | |
| `MailerImportService.ClassifyVerifiedMatch(MailerLiteSubscriber)` | |
| `SurveyController.IsAnswerable(SurveyDetail)` | |
| `ExpenseReportService.ResolveSyncState(HoldedExpenseOutboxEvent)` | |
| `Service.ClassifyStripeSession(StoreCheckoutSessionData)` | |
| `GoogleWorkspaceSyncService.IsAnyUserPermission(DrivePermission)` | |
| `GateScanCardViewModel.TooEarlyReason(GateScanResult)` | |

**≈70% precision at 26 hits across 44 sections.** `DriftedMoreThanTenPercent(ImportPlanCounts)` — a
predicate about a data type, living on a controller — is the textbook case the rule was proposed for.

The residual false positives are a coherent, nameable class: `Render*` / `Format*` / `Display*` /
`Build*Label`. Whether they are false positives at all is arguable — a display name for a `TeamInfo`
is a fact about a `TeamInfo` — but pushing view concerns onto a DTO is a trade many teams refuse, so
they should not be charged without the owner agreeing to that position.

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

**19 of the 24 sections that have a write interface have exactly one.** For those — and for the 20
sections with none — a flat per-interface charge is a *constant*. It moves every score by the same
amount and changes no ranking, which is the one thing a per-section rule exists to do.

Essentially all of the rule's discriminating power on this corpus is a single observation: Users
declares 16 write interfaces, three times the next section. That is a real and interesting finding —
a section publishing sixteen separate write APIs is worth looking at — but it is **n = 1**.

### Question 1 — does it double-charge `fullServiceInterfaceMethod`?

**No, provided the charge is flat per interface.** The two price different decisions:

- `fullServiceInterfaceMethod` scales with **width** — how much write API a section published.
- `publicWriteSurface` would price the **decision to publish a write API at all**, which is a
  separate architectural fact: a section with one write method and a section with thirty have both
  crossed the same line once.

The distinction is only real if the new rule is flat. A per-method or size-scaled charge would be a
weight change on `fullServiceInterfaceMethod` wearing a new name, exactly as #35 suspects.

Modelled cost of a flat per-interface charge, and how it lands:

| weight | added points | share of current surface | Users gets | the other 23 sections get |
|---:|---:|---:|---:|---|
| 5 | 235 | 1.4% | +80 | +5 to +25 |
| 10 | 470 | 2.7% | +160 | +10 to +50 |
| 15 | 705 | 4.1% | +240 | +15 to +75 |
| 25 | 1,175 | 6.8% | +400 | +25 to +125 |

At every weight, a third of the rule's total lands on one section. A weight of 25 — the current
`crossSectionRepository` level — would also make this the 6th largest rule in the system on its
first day, off 47 declarations.

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

**Add a scored `publicWriteSurface`: not yet.** The direction is right and the double-charging
concern is answerable (a flat per-interface charge prices a different decision from a per-method
one; the codebase already prices `newRepositoryInterface` at 15 beside `repositoryInterfaceMethod`
at 10, so the shape has precedent). What cannot be answered from this corpus is the **weight**,
because the distribution is one constant and one outlier. Any weight picked here is fitted to Users.

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
