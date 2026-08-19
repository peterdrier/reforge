# Internal-axis candidate signals — Gate 2 measurements

## Status

Measurement record (2026-08-19). Discharges the Gate 2 obligation in
`2026-08-15-scoring-alignment-design.md` for the two candidate signals issue #19 proposes, and
answers the two open questions issue #35 flags for the `publicWriteSurface` transform.

**No rule is proposed for immediate implementation.** All three signals measured are recommended
*against* in the shape they were proposed — two on precision, one because it cannot be calibrated
from this corpus. One deletion is recommended: `crossSectionWriteSurface` scores 0 on Humans, so
retiring it is a no-op there — though not on the sample solution, which fires it twice.

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

- **Single-caller private helper.** A method declaration whose symbol is `private` and has a body.
  Every `SimpleNameSyntax` in the declaring assembly (excluding the declaration's own name) whose
  resolved symbol is that method's `OriginalDefinition` is attributed to the member enclosing it —
  the nearest `MethodDeclaration` / `ConstructorDeclaration` / `PropertyDeclaration` /
  `AccessorDeclaration` / `LocalFunctionStatement` / `FieldDeclaration` ancestor. The method's caller
  count is the number of **distinct** such members, excluding the method itself (recursion is not a
  caller) and excluding names inside a `nameof(...)` argument (a mention is not a call). Two details are load-bearing: resolving *name
  nodes* rather than invocations is what makes a method group (a delegate assignment, an event
  subscription) count at all, and grouping by enclosing member is what makes "called twice from one
  method" one caller rather than two. Counting per assembly is exact for `private`, which nothing
  outside can reach.
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
  - *scalar result* — return type is an enum or one of `bool`, `string`, `int`, `long`, `decimal`,
    `double`, `float`, `DateTime`, after unwrapping **only** `Task<T>` / `ValueTask<T>`. Not any
    single-argument generic: unwrapping those would reduce `IEnumerable<string>`, `List<int>` or
    `Result<MyEnum>` to their argument and call them scalar, and a method returning a container is a
    projection — the population this refinement exists to exclude. (Correcting this moved the
    intermediate scalar set 38 → 36 and left the refined 26 unchanged, because the container-returning
    methods were async and the synchronous filter had already removed them. The predicate was wrong
    even though the published number was not.)
  - *synchronous* — return type is not `Task` / `ValueTask`.
- **Write surface.** Each section's `fullServiceInterfaces` array from
  `section-shape --format json`, which lists them by **symbol**. No harness needed; it is readable
  off the report. Counting distinct declaring *files* instead — the earlier proxy — undercounts
  wherever two interfaces share a file, and is wrong by roughly a factor of two on this corpus.

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

1,319 **private** methods with bodies, by number of **distinct callers** in their declaring assembly:

| callers | methods | share | median LOC |
|---:|---:|---:|---:|
| 0 | 38 | 2.9% | 6 |
| **1** | **762** | **57.8%** | 15 |
| 2 | 328 | 24.9% | 11 |
| 3 | 79 | 6.0% | 10 |
| 4 | 34 | 2.6% | 8 |
| 5+ | 78 | 5.9% | 8 |

360 of the 762 single-caller helpers are under 15 LOC.

**Three corrections from review, all of which this table already reflects.** The first pass measured
the wrong population, undercounted references, and then counted the wrong thing entirely:

- It included `internal` and `protected internal` while counting only within the declaring assembly.
  Neither is assembly-private in practice — `internal` is reachable from friend assemblies through
  `InternalsVisibleTo` (the test projects this walk excludes), `protected internal` by deriving from
  another assembly — so their callers could not be counted from here at all. #19 proposes a
  **private** helper rule; that is now the population.
- It counted invocations plus argument-position identifiers, which misses a method group used as a
  delegate or event handler (`x.Changed += Bar`, `Foo = Bar`). Counting *name nodes* catches call
  sites and method groups alike, once each.

- It counted **references**, not **callers**. A helper invoked twice from one method has one caller
  and landed in the two-reference bucket — which understates exactly the population #19's rule
  targets. References are now grouped by the member enclosing them.
- It counted two kinds of reference that are not calls: `nameof(Helper)`, which mentions a method
  without calling it, and a **recursive** self-reference, which makes a helper its own caller. The
  first can put a never-called helper in the one-caller bucket; the second pushes a helper called
  from exactly one other member out of it. Both are excluded. (Worth 2 methods on this corpus —
  57.6% → 57.8% — but they distort in opposite directions, so the small net says nothing about
  either.)

The population and counting fixes moved the figure barely (48.9% → 48.5%). Grouping by caller moved
it a lot: **57.8%**. Worth separating those, because they say different things. The first two were
flaws that happened not to matter in aggregate; the third was measuring a different quantity than the
one under discussion, and correcting it made the finding *stronger*.

### Reading

**The base rate is 57.8%.** Well over half of all private methods in this codebase have exactly one
caller — in a corpus nobody has accused of extract-method fragmentation, and much of which predates
any LLM involvement.

A rule charging per standing single-caller helper would therefore charge the majority of all
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

| genuine feature envy (19) | presentation / formatting (7) |
|---|---|
| `ProfileCompletion.ComputePercent(ProfileInfo)` / `(Profile)` | `AgentPromptAssembler.BuildUserContextTail(AgentUserSnapshot)` |
| `GateAdmissionRules.Evaluate(GateScanContext)` | `AgentPromptAssembler.RenderShiftEntry(UpcomingShiftEntry)` |
| `FeedbackService.NeedsReply(FeedbackReport)` | `ProfileApiController.FormatContactFieldDetail(ContactFieldDto)` |
| `ShiftManagementService.CalculateScore(Shift)` | `PersonSearchMatcher.DisplayLabel(ContactFieldInfo)` |
| `GoogleWorkspaceSyncService.IsDirectManagedPermission(DrivePermission)` | `TeamAdminController.BuildResourceLinkError(LinkResourceResult)` |
| `GoogleWorkspaceSyncService.IsAnyUserPermission(DrivePermission)` | `SurveyCsvExportBuilder.QuestionHeader(SurveyExportQuestion)` |
| | `ProfileCardViewComponent.GetDisplayName(TeamInfo)` |
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

**≈73% precision at 26 hits across 44 sections** (19 + 7 — an earlier draft listed only 25 because
the harness table was capped at 25 rows while the count said 26, so `GetDisplayName` was measured but
never displayed; the audit is now over all of them). `SurveyBranchingEvaluator.IsVisible(BranchCondition)`
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

Counted from `section-shape --format json`, which lists each section's full-service interfaces by
**symbol**. An earlier draft of this document counted distinct *files* carrying a
`fullServiceInterfaceMethod` entry, which collapses two interfaces declared in one file into one —
a layout this repo's own sample solution uses (`ICampService` and `ICampRequestService` share
`CampFixtures.cs`). That proxy was wrong by roughly a factor of two, and correcting it reverses the
recommendation below.

| | file proxy (wrong) | interface symbols (correct) |
|---|---:|---:|
| write-capable service interfaces | 47 | **93** |
| sections declaring at least one | 24 of 44 | **37 of 45** |

Methods on them: 517, already charged **4,136 points** by `fullServiceInterfaceMethod` at 8/method.

The distribution is the part that matters, and it is not the shape the earlier draft described:

| write interfaces in the section | sections |
|---:|---:|
| 1 | 18 |
| 2 | 9 |
| 3 | 5 |
| 4 | 1 |
| 6 | 1 |
| 7 | 1 |
| 9 | 1 |
| 16 | 1 (Users) |

**This is a graded distribution, not a binary split with one outlier.** Half the positive sections
have more than one write interface, and the tail runs 3, 4, 6, 7, 9, 16 rather than jumping from 1 to
16. Users is the top of a range, not a lone anomaly.

### Question 1 — does it double-charge `fullServiceInterfaceMethod`?

**Not necessarily, and the answer differs by reading.**

- `fullServiceInterfaceMethod` scales with **width** — how much write API a section published, by
  method. 517 methods, 4,136 points.
- A **per-interface** charge measures **fragmentation** — how many separate write APIs a section
  publishes. 93 interfaces. A section with one 30-method interface and a section with fifteen
  2-method interfaces have the same width and very different shapes, and only this rule can tell
  them apart.
- A **per-section** charge measures the binary decision to publish write capability at all.

Fragmentation and width are correlated but distinct, so a per-interface charge is not simply a
weight change on the existing rule — which is the concern #35 raises, and it is answerable on the
data rather than by argument.

### Question 2 — what happens to `crossSectionSuppress`?

`ScoreAsync` maintains a suppression set purely so `crossSectionWriteSurface` can pre-empt
`writeCapableInterfaceUsedReadOnly` for the same caller/dependency pairs. Since the old rule fires
**0 times on 45 sections of Humans**, that set is empty there, and `writeCapableInterfaceUsedReadOnly`
scores 60 points across 5 files in 3 sections regardless.

**On Humans, retiring the rule and its suppression set is a measured no-op.** It is worth being
precise about the scope of that claim, because an earlier draft of this document was not:
"0/44, therefore free" is true of Humans and false in general.

The **sample solution scores it 30** — two fixtures, `ReadOnlyGreetingConsumer` in Services and
`CampReportBuilder` in Reporting, both deliberately built to fire it. So retiring the rule is not a
no-op there, and three things travel with it:

- the two fixtures lose their reason to exist in their current form;
- `crossSectionWriteSurface` has a `NotYetCovered` entry in `GateOneFixtureTests` that would have to
  go with it — and that entry's stated reason ("the unverified advisories from a real corpus decide
  whether the rule needs a fixture or a repair") is exactly what this measurement answers;
- removing the suppression set means `writeCapableInterfaceUsedReadOnly` (12) can fire on pairs the
  specialised rule (15) used to claim. Zero such pairs on Humans; two on the sample solution.

None of that argues against retiring it. It argues that the retirement is a small change with
fixture work attached, not a free deletion, and that whoever does it should measure the sample
solution as well as Humans — a rule that fires nowhere in the field but twice in the fixtures is
precisely the case where one corpus is not enough.

### Modelled cost

| weight | per section (37 charges) | per interface (93 charges) |
|---:|---:|---:|
| 5 | 185 (1.1%) | 465 (2.7%) |
| 10 | 370 (2.1%) | 930 (5.4%) |
| 15 | 555 (3.2%) | 1,395 (8.0%) |
| 25 | 925 (5.3%) | 2,325 (13.4%) |

Percentages are of the current 17,379-point surface. Under the per-interface reading Users takes
16 of 93 charges — **17%** of the rule's output, not the third the file-proxy figures implied.

### Decision

**Per interface, not per section — which reverses this document's earlier recommendation.**

The reversal is entirely due to the count. On the file proxy the distribution looked binary (19 of 24
sections at exactly one), which made a per-section charge look like the discriminating reading and a
per-interface charge look like an outlier detector for Users. On the real counts:

- **Per section fires on 37 of 45 — 82% of the solution.** A charge that lands on four sections in
  five is close to a constant: it shifts nearly every score by the same amount and changes almost no
  ranking, which is the one thing a per-section rule exists to do.
- **Per interface has a real distribution** — 1 through 16, with half the positive sections above 1 —
  so it discriminates, and it measures fragmentation, which nothing else in the config measures.

The `n = 1` objection the earlier draft raised does not survive the corrected count either: 16 is the
top of a graded tail, not a lone spike.

**The weight remains policy**, as every weight in the config is, but it is now calibratable against a
real distribution rather than fitted to one section. A weight of 25 would make this the fourth
largest rule in the system on its first day; 5 to 10 keeps it informative without dominating.

**Still recommended as reported-before-scored**, but for a narrower reason than before: not because
the signal cannot be calibrated, but because a rule that fires on 82% of sections in its *binary*
form and on 93 declarations in its *per-interface* form deserves one look at a second corpus before
it ships. Surfacing the per-section interface count in the `metrics` block added by #45 costs nothing
and makes that second reading free to take.

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
