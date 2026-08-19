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

Every number here is measured. Nothing is extrapolated. Every figure that review moved is recorded
with what moved it — including one recommendation that flipped and flipped back across three passes at
the same population, which is the most transferable thing in this document.

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
| corpus | 45 configured sections (44 score anything — `Tour` is empty), 3,448 types, 157,860 prod LOC, 5,523 method bodies |
| score | `surfaceTotal` 17,379, `internalComplexityTotal` 3,113, `degraded: false` |

The harness is not committed, so the **signal definitions are stated as predicates** below rather
than left implicit in code that no longer exists. Each is a few lines of Roslyn over the same walk:

- **Single-caller private helper.** A method declaration whose symbol is `private`, has a body, and
  has **no `ExplicitInterfaceImplementations`**. That last exclusion is load-bearing: Roslyn reports
  an explicit interface implementation as `private`, but it is externally callable and calls to it
  bind to the *interface* member rather than to this `OriginalDefinition`, so every one of them lands
  in the zero-caller bucket while not being a private helper at all.
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
  `MemberAccessExpressionSyntax` nodes whose **receiver resolves to that first parameter**, plus
  `MemberBindingExpressionSyntax` nodes whose conditional receiver does (`p?.Foo` is
  `ConditionalAccessExpression(p, MemberBinding(.Foo))`, not a member access, so a predicate that
  looks only for member accesses misses every null-conditional read — the form nullable DTO and
  entity handling uses constantly);
  `selfTouches` counts explicit `this.X` accesses plus unqualified names resolving to an instance
  member of the containing type (implicit `this`); `otherTouches` counts accesses through any *other*
  receiver — another parameter, a local, a field, an injected dependency. **An extension call on the
  parameter (`p.Normalize()`) is not a target touch**: the receiver resolves to the parameter, but the
  member lives on an unrelated static class and is not owned by the target type, so it counts toward
  `otherTouches` instead. Fires when `targetTouches >= 3 && targetTouches >= selfTouches * 2` — `>=` on
  the ratio, matching "at least twice as often"; the first cut used strict `>`, which silently rejected
  the exact two-to-one boundary the prose admits. Binding to the receiver rather than to the
  member's declaring type is load-bearing — see the correction recorded under Signal B.
  - **Two of #19's own scope conditions are measured separately rather than built into the firing
    test, because both turn out to matter more than the firing test does.** #19 scopes the signal to
    methods whose primary parameter is "an entity or DTO" and which "touch **only** that type's
    members". The firing test enforces neither: it admits any solution-declared class or struct, and
    it compares target touches against *self* touches only, ignoring `otherTouches` entirely. Both
    are reported under "#19's scope conditions, measured" below, and the exclusivity one changes the
    headline result.
  - *entity or DTO* — **not tested; approximated by a data-carrier proxy** whose error is known and
    bidirectional. The proxy is "declares no ordinary methods of its own beyond object overrides and
    `Deconstruct`". It rejects real entities that carry domain methods (`Shift`, `EventSettings` — see
    below) and accepts property-only contexts and view models, which are not entities or DTOs. It is
    deliberately not reforge's `*Dto` / `*Info` / `*Request` name patterns either, since this same
    document measures that style of classification at 79% precision on this corpus, so scoping a
    precision measurement with it would be circular. **Neither available test is the stated one**, so
    the entity/DTO condition is reported as unmeasured rather than as satisfied.
  - *mapper* — the method's return type (unwrapped one level through `Task<T>` / `ValueTask<T>` /
    a single-type-argument generic) differs from the parameter's type, and the body contains an
    `ObjectCreationExpressionSyntax` or `ImplicitObjectCreationExpressionSyntax` of that return type.
  - *scalar result* — return type is an enum or **any primitive `SpecialType`** (`bool`, `string`,
    `char`, `sbyte`, `byte`, `short`, `ushort`, `int`, `uint`, `long`, `ulong`, `decimal`, `double`,
    `float`, `DateTime`), after unwrapping `Task<T>` / `ValueTask<T>` **and `Nullable<T>`**. The first
    cut listed eight primitives that came to mind, which silently excluded `byte`, `short`, `char`,
    `uint`, `ulong` and every nullable primitive or enum — all of which have exactly the
    answers-a-question-about-the-parameter shape this filter selects for. `Nullable<T>` is the one
    generic worth unwrapping alongside the await wrappers, because `bool?` and `MyEnum?` are the same
    answer with a third "unknown" case, whereas `List<T>` is a container. **Correcting this changed
    nothing on this corpus** — the intermediate and refined sets kept identical membership — because
    Humans returns none of the omitted primitives from a feature-envy candidate. Recorded because the
    predicate was wrong even though the number was right, which is the second time that has happened
    to this same filter. Not any other single-argument generic: unwrapping those would reduce `IEnumerable<string>`, `List<int>` or
    `Result<MyEnum>` to their argument and call them scalar, and a method returning a container is a
    projection — the population this refinement exists to exclude. (Correcting this moved the
    intermediate scalar set down by two and left the refined set's membership unchanged, because the container-returning
    methods were async and the synchronous filter had already removed them. The predicate was wrong
    even though the published number was not.)
  - *synchronous* — return type is not `Task` / `ValueTask`.
- **Write surface.** Each section's `fullServiceInterfaces` array from `section-shape --format json`
  — by **symbol**, not by declaring file — **filtered to `IsExported` types**. Both halves matter and
  each was got wrong in turn. Counting declaring *files* undercounts wherever two interfaces share a
  file. Counting every classified symbol **over**counts, because `SectionShapeAnalyzer` builds
  `FullServiceInterfaces` from the unfiltered classified types while `SurfaceScoreEngine` skips
  non-exported types before charging `fullServiceInterfaceMethod` — and on this corpus 46 of the 93
  classified write interfaces are `internal`, so they publish nothing and score nothing. The exported
  set was then cross-checked a second way, by mapping every charged `fullServiceInterfaceMethod` entry
  in `surface-score --format json --all` back to the interface declared in its file; both routes give
  the same 47.

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
- `crossSectionWriteSurface` still scores **0 across all 44 scored sections**, confirming #35's reading on a
  second, larger corpus.

---

## Signal A — single-caller non-public helper

**Proposed by #19 as:** "Single-caller private helper (net-new). Directly measures the
extract-to-satisfy-the-linter artifact. Needed as a counterweight if any size rule is retained."

### Distribution

1,288 **private** methods with bodies, by number of **distinct callers** in their declaring assembly:

| callers | methods | share | median LOC |
|---:|---:|---:|---:|
| 0 | 7 | 0.5% | 9 |
| **1** | **762** | **59.2%** | 15 |
| 2 | 328 | 25.5% | 11 |
| 3 | 79 | 6.1% | 10 |
| 4 | 34 | 2.6% | 8 |
| 5+ | 78 | 6.1% | 8 |

360 of the 762 single-caller helpers are under 15 LOC.

**Four corrections from review, all of which this table already reflects.** The first pass measured
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
- It included **explicit interface implementations**, which Roslyn reports as `private` even though
  they are externally callable and calls to them bind to the interface member, not to the
  implementation. 31 of them were in the population and essentially all of them sat in the
  zero-caller bucket — which is why that bucket collapsed from 38 methods to **7** when they were
  excluded. The numerator did not move at all (762 either way); the denominator did, 1,319 → 1,288,
  taking the base rate from 57.8% to **59.2%**.

The population and counting fixes moved the figure barely (48.9% → 48.5%). Grouping by caller moved
it a lot: 57.8%. Excluding explicit interface implementations moved it again, to **59.2%**. Worth
separating those, because they say different things. The first two were flaws that happened not to
matter in aggregate; grouping by caller was measuring a different quantity than the one under
discussion; and the explicit-implementation exclusion removed a population that was never eligible.
Every correction that moved the number moved it the same way — the finding got *stronger* each
time it was made more careful, which is the opposite of the pattern that should worry a reader.

### Manual audit of the top hits

Gate 2 asks for a read of the top hits for false positives. A per-helper rule charges every hit
equally, so "top" has no natural order; the largest are the most informative (the biggest thing the
rule would claim is an artifact), and an evenly-spaced sample checks that the tail does not differ.

**Top 15 of the 762 by LOC:**

| method | LOC | | method | LOC |
|---|---:|---|---|---:|
| `AgentService.RunTurnAsync` | 210 | | `StoreWebhookRegistrationService.RegisterAsync` | 79 |
| `ExpenseReportService.ProcessHoldedCreateAsync` | 145 | | `ShiftVolunteerSearchBuilder.BuildAsync` | 77 |
| `LegalDocumentSyncService.SyncFolderBasedDocumentAsync` | 102 | | `LegalDocumentSyncRunner.SendReConsentNotificationsAsync` | 76 |
| `ShiftManagementService.ComputeCoordinatorActivityAsync` | 93 | | `ShiftManagementService.BuildDepartmentRows` | 71 |
| `TicketTransferService.WriteToVendorAsync` | 88 | | `BudgetAdminController.BuildCashFlowModel` | 70 |
| `TicketSyncService.SyncEventParticipationsAsync` | 87 | | `AttendeeContactImportService.ClassifyAsync` | 70 |
| `HumansMetricsService.RefreshSnapshotAsync` | 85 | | `GoogleWorkspaceSyncService.PopulateActualSettings` | 66 |
| | | | `WorkloadService.BuildByPersonAsync` | 66 |

**0 of 15 is the shape the rule targets.** Every one is a substantial named operation — a sync step, a
vendor write, a snapshot refresh, a model build. #19 proposes this signal to detect "the
extract-to-satisfy-the-linter artifact"; a 210-line `RunTurnAsync` with one caller is the opposite of
that artifact. Charging these would point an agent at inlining 66-to-210-line named operations back
into their callers, producing exactly the methods the size rules already charge for. The signal's top
hits argue *against* the signal.

**Evenly-spaced sample of 15, LOC descending**, to reach the small end where genuine artifacts would
live: `AgentService.RunTurnAsync` (210), `AgentAdminStatusService.BuildUsage` (42),
`DevLoginController.ResolveSeededPersonaUserAsync` (33),
`DriveActivityMonitorService.GetPermissionTargetAsync` (28),
`AuditLogViewComponent.ParseColumnLabels` (24), `BudgetAdminController.GroupByMonth` (20),
`SectionDiscoveryExtensions.DiscoverSections` (17), `CityPlanningService.ToDto` (15),
`PreMigrationSnapshot.DiscardAbandonedWrites` (13),
`VolunteerTrackingXlsxBuilder.WriteDayHeaders` (12),
`MailerAudienceDebugSnapshotBuilder.NotificationTargetEmail` (10),
`OrderAuthorizationHandler.TodayInEventZoneAsync` (8), `CantinaRosterService.BuildWeekDays` (6),
`MailerLiteSubscriberConverter.ReadString` (4), `DietaryMedicalViewModel.IsKnownIntolerance` (1).

The small end is where an extraction artifact *could* hide — and it is where the audit runs out of
discriminating power. `IsKnownIntolerance` is one line with one caller; it is also a named domain
predicate. `ReadString` is four lines inside a JSON converter. `TodayInEventZoneAsync` is eight lines
of timezone arithmetic. Nothing structural separates "extracted to shorten a method" from "named
because the concept deserves a name" — the difference is *when* it was written and *why*, which is
history, not structure.

That is the audit's real finding, and it is why the delta framing is the only defensible one: the
signal is undecidable on a snapshot at exactly the sizes where it would matter, and wrong at the sizes
where it is decidable.

### Reading

**The base rate is 59.2%.** Well over half of all private methods in this codebase have exactly one
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

**And the delta form has not been through Gate 2.** What is discharged here is the signal *as #19
proposes it*, measured as a stock, and the answer is a rejection with a stated successor. The successor
is a different rule with a different population — helpers that appear in a diff — and nothing measured
here establishes its precision. A 59.2% stock base rate says the stock is not chargeable; it says
nothing about whether newly-added single-caller helpers are linter-driven extraction or intended
decomposition, and the audit above suggests that distinction may not be decidable from structure at
all. So the delta rule needs its own Gate 2 read — helper-set diffs across real changes, with a manual
read of what shows up — before it earns a weight. Recording that explicitly because "confirmed as a
delta signal" could otherwise be read as "cleared to implement", and it is not.

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

**363 candidates.**

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

- **172 of 363 (47.4%) are mappers** by that structural test.
- 191 remain.

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
implicit-`this` names count toward `selfTouches`. A later round added the null-conditional form —
`p?.Foo`, which Roslyn models as a `MemberBindingExpressionSyntax` under a conditional access rather
than as a member access, so a member-access-only predicate misses every one of them. Raw candidates
fell 385 → 360 under receiver binding and rose to 361 once null-conditional reads were counted, and
the mapper share barely moved, but **membership changed at the margins** — the case I had held up as the textbook hit,
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

**363 → 191 → 35 → 25 candidates.**

Manual read of all 25:

| genuine feature envy (18) | presentation / formatting (7) |
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

**≈72% precision at 25 hits across the corpus** (18 + 7). Two bookkeeping corrections got the audit to
cover exactly the population: an earlier draft listed only 25 of 26 because the harness table was capped
at 25 rows while the count said 26, so `GetDisplayName` was measured but never displayed; and
`ShouldHideTimeLabel` later left the population altogether once extension calls stopped counting as
target touches. `SurveyBranchingEvaluator.IsVisible(BranchCondition)`
and `UserStateClassifier.Classify(User)` are the shape the rule was proposed for: a predicate or
classification about a data type, living somewhere else.

Two things about the residue:

- The false positives are a coherent, nameable class — `Render*` / `Format*` / `Display*` /
  `Build*Label`. Whether they are false positives at all is arguable: a display name for a `TeamInfo`
  is a fact about a `TeamInfo`. But pushing view concerns onto a DTO is a trade many teams refuse, so
  they should not be charged without the owner taking that position first.
- **Extension methods: now zero, and the reason is instructive.** An earlier round found one
  (`CalendarOccurrenceViewExtensions.ShouldHideTimeLabel`) and concluded that a scored rule must exclude
  extension methods, since an extension method is C#'s idiom for "this belongs on the type and cannot be
  put there" — the fix already applied as far as the language allows, so charging it charges someone for
  having complied. That conclusion stands and is now *structural* rather than a special case: once
  extension **calls** stopped counting as target touches, `ShouldHideTimeLabel`'s own touches on its
  parameter turned out to be extension calls, and it fell below the threshold and left the population.
  The two exclusions are the same principle seen from both ends — members a type does not own are not
  that type's members, whether the candidate method is the extension or merely calls one.

### #19's scope conditions, measured — and one of them changes the answer

#19 scopes the signal twice over, and neither condition is in the firing test:

> "For each **entity or DTO**, count external methods whose primary parameter is that type and which
> **touch only that type's members**"

Both measured against all three populations:

| population | data-carrier param *(proxy, not the condition)* | **no other-receiver touches** | both | other-receiver touches exceed target |
|---|---:|---:|---:|---:|
| raw (363) | 308 (85%) | **75 (21%)** | 67 | 133 (37%) |
| non-mapper (191) | 171 (90%) | **14 (7%)** | 11 | 88 (46%) |
| refined (25) | 23 (92%) | **2 (8%)** | 2 | 3 |

**The entity/DTO condition is not established, and the numbers above do not establish it.** 85% of
raw candidates and 23 of the refined 25 pass the *data-carrier proxy* — but the proxy is not the
condition. It errs in both directions:

- **False negatives.** `Shift` (`CalculateScore`) and `EventSettings` (`GetAvailableEeSlots`) are real
  EF entities that declare domain methods, so the proxy rejects them. They are squarely in #19's
  scope; adopting the proxy as the gate would discard them.
- **False positives.** A property-only context or view model passes the proxy while being neither an
  entity nor a DTO — and `GateScanContext`, top of the refined list, is exactly that shape.

So "23 of 25" measures agreement with a proxy, not compliance with #19's scope, and it cannot support
"this condition is free". What it does establish is narrower and still useful: the refined population
is not dominated by service or dependency parameters, which was the specific failure mode worth ruling
out. **Adopting the entity/DTO condition needs a real classification first** — and the obvious
candidate, reforge's own DTO name patterns, is the thing #54 shows to be 79% precise, so that
classification is itself unbuilt work rather than a lookup.

**The exclusivity condition is not free at all — it is the whole finding.** Read literally, "touch
only that type's members" leaves **2 of the 25**: `GateAdmissionRules.Evaluate(GateScanContext)` and
`UserService.HasRequiredNameFields(Profile)`. Everything else does at least some work through another
receiver. The worst case in the refined set is
`AgentPromptAssembler.BuildUserContextTail(AgentUserSnapshot)` at **33 other-receiver touches against
18 target touches** — it does nearly twice as much work elsewhere as on the parameter it supposedly
envies, and moving it onto `AgentUserSnapshot` would drag all of that onto a DTO.

So the refinement's headline depends entirely on how "touch only" is read:

| reading of "touch only that type's members" | refined candidates |
|---|---:|
| literal — zero other-receiver touches | **2** |
| `otherTouches <= targetTouches` | 22 |
| ignored, as the firing test does | 25 |

**The 25 figure elsewhere in this document is the third row.** It is a real population and the manual
audit of it stands, but it is a *looser* rule than #19 specifies, and this table is the honest version
of the precision claim. Under #19 as written the signal fires **twice** on a 157,860-line corpus — which
is a different problem from imprecision, and a worse one for a rule meant to carry an axis.

### Decision

**Reject as specified in #19. Recommend the refined form for a follow-up measurement round, not yet
for a weight.**

Concretely:

- The rule as written in #19 would spend most of its points telling an agent to invert a dependency.
  This is the same class of result as the rejected duplication rule: a plausible signal whose top
  hits are the wrong thing.
- The refined form (non-mapper + scalar/bool/enum/string return + synchronous) is a usable signal at
  ~72% precision and a tractable 25 hits — **but only under the loose reading of "touch only that
  type's members"**. Under #19's literal reading it fires twice on 157,860 lines. Whoever takes the
  next round has to pick a reading first, because the two differ by more than 12×, and the loose one is
  what the ~72% precision figure was measured against.
- Before it earns a weight it needs: **a decision on how strictly to read exclusivity** (2 hits or 22
  or 25), a decision on the `Render*`/`Format*` class, and a re-measure against a second corpus,
  because 25 hits on one codebase is a thin basis for a weight and 2 is no basis at all.
- The entity/DTO scope condition **cannot** be adopted yet. 23 of the 25 pass a data-carrier proxy,
  but that proxy rejects entities with domain methods (`Shift`, `EventSettings`) and accepts
  property-only contexts (`GateScanContext`), so adopting it would discard true hits while keeping
  out-of-scope ones. It needs a real entity/DTO classification, which reforge does not currently have
  — its name-pattern version is what #54 measures at 79%.
- If it lands as a **credit** rather than a penalty, precision matters more, not less — a credit
  firing on a mapper pays for a dependency inversion. The refined form is the only version safe to
  consider as a credit.

---

## Signal C — `publicWriteSurface` (issue #35)

#35 proposes retiring `crossSectionWriteSurface` — a three-condition conjunction that measures
**0/44 scored sections** — and replacing it with a declaration-side rule that charges for *exporting* write
capability at all. It flags two things to decide. Both are measurable, so both are measured.

### Population

**47 exported write-capable service interfaces across 24 of the 44 scored sections**, carrying
**292 charged methods** worth **4,136 points** under `fullServiceInterfaceMethod`.

This number took three passes to get right, and the intermediate one is worth recording because it
briefly reversed the recommendation:

| pass | predicate | interfaces | sections |
|---|---|---:|---:|
| 1 | distinct files carrying a `fullServiceInterfaceMethod` entry | 47 | 24 of 44 |
| 2 | every symbol in `section-shape`'s `fullServiceInterfaces` | 93 | 37 of 45 |
| 3 | **those symbols filtered to `IsExported`** | **47** | **24 of 44** |

Pass 1 was a proxy: it collapses two interfaces sharing a file into one, a layout this repo's own
sample solution uses (`ICampService` and `ICampRequestService` share `CampFixtures.cs`). Pass 2 fixed
that and counted symbols — but counted *all* classified symbols, and `SectionShapeAnalyzer` builds
`FullServiceInterfaces` from the unfiltered classified types while `SurfaceScoreEngine` skips
non-exported types before charging anything. **46 of those 93 interfaces are `internal`** —
`IShiftManagementService`, `ITicketService`, `ICampService` and most of Humans' other headline
service interfaces are internal to their section, which is the architecture working as intended. An
internal interface publishes no write capability outside its assembly and scores nothing, so a rule
about *exported* write surface must not count it.

Pass 3 was verified a second, independent way: every `fullServiceInterfaceMethod` entry in
`surface-score --format json --all` was mapped back to the interface declared in its file. That route
gives the same 47 interfaces in the same 24 sections, and it is the set the engine actually charges.

Pass 1 landed on the right number for the wrong reason — the exported write interfaces in this
codebase each live alone in a `*.Contracts` file, so counting files happened to count them. A proxy
that is accidentally right is still a proxy, and it is only visible as accidental once the correct
predicate exists.

The distribution over the exported set:

| write interfaces in the section | sections |
|---:|---:|
| 1 | 19 |
| 2 | 2 |
| 3 | 1 |
| 5 | 1 |
| **16** | **1** (Users) |

**This is a binary split with a single outlier.** Nineteen of the 24 positive sections publish exactly
one write interface; the next three are 2, 2 and 3; then GoogleIntegration at 5; then Users at 16.

One correction to a figure this document carried through both earlier passes: the method count is
**292**, not 517. 517 came from counting methods across all 93 classified interfaces. The 4,136 points
are unchanged and always were — they are the engine's own total, and at 292 charged methods they
reflect the per-section multipliers, not a flat 8 per method.

### Manual audit of the population — 79% precision

Gate 2 requires reading the top hits for false positives, and this population is classified by **name
pattern**, which makes that read load-bearing rather than a formality. `fullServiceInterface` is
assigned by `I*Service`; `readServiceInterface` only catches `I*ServiceRead`, `I*ReadService` and
`I*QueryService`. Anything named `I…Service` that happens to be read-only is therefore classified as
write-capable surface.

Read all 47. The top 10 by charged method count are all genuine — `IUserEmailService` (42 methods,
`AddEmailAsync`/`SetPrimaryAsync`/`DeleteEmailAsync`/…), `IUserService` (35, `SaveProfileAsync`/
`AnonymizeProfileForDeletionAsync`/…), `ITeamResourceService` (21, `LinkDriveFolderAsync`/
`UnlinkResourceAsync`/…), then `IRoleAssignmentService`, `ICommunicationPreferenceService`,
`IHoldedFinanceService`, `IGoogleSyncService`, `ITeamService`, `IContainerService`,
`IAccountMergeService`. No false positives in the head.

**The tail is where the misses are. 10 of 47 publish no write capability at all:**

| interface | section | methods | what it actually is |
|---|---|---:|---|
| `IAuditViewerService` | AuditLog | 6 | all `Get*` — audit log reads |
| `IBurnSettingsService` | Shifts | 2 | `GetActiveAsync`, `GetByIdAsync` |
| `IAdminDashboardService` | Web | 2 | `GetAdminDashboardAsync`, `GetPendingReviewCountAsync` |
| `ILegalDocumentService` | Consent | 2 | `GetAvailableDocuments`, `GetDocumentContentAsync` |
| `IICalFeedService` | Calendar | 2 | `GetFeedItemsAsync`, `GetFeedIcsAsync` |
| `IEarlyEntryService` | EarlyEntry | 2 | `GetRosterAsync`, `GetForUserAsync` |
| `IGoogleTranslationService` | GoogleIntegration | 1 | `TranslateAsync` — a pure function |
| `IDashboardService` | Web | 1 | `GetMemberDashboardAsync` |
| `IGdprExportService` | Gdpr | 1 | `ExportForUserAsync` |
| `IAdminAuthorizationService` | Auth | 1 | `RequireCurrentUserIsAdminAsync` — a check |

One near-miss kept as genuine: `IDriveActivityMonitorService.CheckForAnomalousActivityAsync` reads
like a query, but its own doc comment says it "logs anomalous changes to the audit log". It writes.

**Precision as a write-surface proxy: 37 of 47 = 79%.** The audit does not weaken the recommendation
— it sharpens it:

| | as classified | genuine write surfaces |
|---|---:|---:|
| interfaces | 47 | **37** |
| positive sections | 24 of 44 | **18 of 44** |
| distribution | 19×1, 2×2, 3, 5, 16 | **15×1, 2, 4, 16** |
| sections at exactly one | 79% | **83%** |
| Users' share of a per-interface charge | 34% | **43%** |

Six sections drop out entirely (Calendar, Consent, EarlyEntry, Gdpr, Shifts, Web) because their only
`I*Service` was read-only. The distribution is *more* binary-with-one-outlier than the unaudited
count, and Users would take 43% of a per-interface rule's output rather than 34%. Every conclusion
below holds on either population; the per-section reading holds more strongly on the audited one.

**A consequence beyond this signal.** Those 10 interfaces are charged `fullServiceInterfaceMethod`
**today** — 20 methods, **216 points, 5.2% of the rule's 4,136**. That is a live scoring inaccuracy,
not a measurement artifact: read-only interfaces are being priced as published write surface because
they are named `I…Service`. Filed separately as **#54**, since fixing the classifier or the config is
outside a measurement record. It is also the strongest argument in this document for why Gate 2 asks
for the manual read: the aggregate looked fine, and 21% of the population was the wrong thing.

### Question 1 — does it double-charge `fullServiceInterfaceMethod`?

**Not necessarily, and the answer differs by reading.**

- `fullServiceInterfaceMethod` scales with **width** — how much write API a section published, by
  method. 292 methods, 4,136 points.
- A **per-interface** charge measures **fragmentation** — how many separate write APIs a section
  publishes. 47 interfaces. A section with one 30-method interface and a section with fifteen
  2-method interfaces have the same width and very different shapes, and in principle only this rule
  can tell them apart.
- A **per-section** charge measures the binary decision to publish write capability at all.

Fragmentation and width are distinct in principle, so a per-interface charge is not *automatically*
the weight change on the existing rule that #35 worries about. On this corpus, though, the two barely
separate: 19 of 24 positive sections have exactly one write interface, so for 79% of them
fragmentation is a constant and the rule reduces to the per-section reading with extra machinery.
Only Users, GoogleIntegration and three others differ at all — and Users would take 16 of 47 charges,
**34% of the rule's entire output**, for what is architecturally one decision made once. That is the
weight change wearing a new name.

### Question 2 — what happens to `crossSectionSuppress`?

`ScoreAsync` maintains a suppression set purely so `crossSectionWriteSurface` can pre-empt
`writeCapableInterfaceUsedReadOnly` for the same caller/dependency pairs. Since the old rule fires
**0 times on all 44 scored sections of Humans**, that set is empty there, and `writeCapableInterfaceUsedReadOnly`
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

Costed on the **audited** population — 18 sections, 37 interfaces — because that is what the rule is
meant to price. The classified-set columns are shown alongside, since a naive implementation reading
`fullServiceInterfaces` would produce them:

| weight | per section, audited (18) | per section, as classified (24) | per interface, audited (37) | per interface, as classified (47) |
|---:|---:|---:|---:|---:|
| 5 | 90 (0.5%) | 120 (0.7%) | 185 (1.1%) | 235 (1.4%) |
| 10 | 180 (1.0%) | 240 (1.4%) | 370 (2.1%) | 470 (2.7%) |
| 15 | 270 (1.6%) | 360 (2.1%) | 555 (3.2%) | 705 (4.1%) |
| 25 | 450 (2.6%) | 600 (3.5%) | 925 (5.3%) | 1,175 (6.8%) |

Percentages are of the current 17,379-point surface. The two per-section columns differ by a third,
which is the size of the #54 defect expressed as a weight — worth seeing before anyone picks one.
Under the per-interface reading Users alone takes 16 of 37 audited charges — **43%** of the rule's
output.

### Decision

**Per section, not per interface** — and reported before scored.

This document reversed itself once here and is now back where it started, so the reasoning is worth
laying out rather than just the answer:

- The **first** pass counted declaring files, saw a binary distribution, and recommended per section.
  Right answer, unsound predicate.
- The **second** pass counted every classified interface symbol, saw a graded 1-to-16 distribution
  across 82% of sections, and reversed to per interface. Sound predicate, wrong population — it
  counted 46 `internal` interfaces that export nothing and score nothing.
- The **third** pass filtered to exported symbols and cross-checked against the entries the engine
  actually charges. The distribution is binary-with-one-outlier again, and per section is the
  defensible reading again.

On the corrected and audited count:

- **Per section fires on 18 of 44 — 41% of scored sections** (24 of 44, 55%, on the unaudited
  classified set). Either way it is a real split rather than a near-constant: it separates the
  sections that publish write capability from the sections that do not, which is exactly what #35's
  crossed-the-line rationale asks for, and it is orthogonal to `fullServiceInterfaceMethod`, which
  prices width by method.
- **Per interface would charge Users 16 times** — 43% of the rule's total output on the audited
  population — for a single architectural decision, and would be a near-constant for the 83% of
  positive sections that have exactly one interface. It measures fragmentation only in the three
  sections where fragmentation varies at all.

The `n = 1` calibration objection therefore stands, and applies to the **per-interface** reading only:
any weight calibrated against a tail of one would in fact be calibrated against Users. Under the
per-section reading there is no outlier problem, and what remains is the milder objection that one
corpus cannot say whether 41% prevalence is normal or high.

**Recommended as reported, not scored** — a category the 2026-08-15 spec already has — pending a
second corpus, **and blocked behind #54 in either form**. That last condition is not a formality: the
prevalence this rule would report is 18 of 44 and the prevalence the classified set yields is 24 of
44, so implementing the metric off `fullServiceInterfaces` today would report six sections as
publishing write capability when their only `I*Service` is read-only. It would ship the #54 defect
into a new metric and calibrate a future weight against an inflated denominator. So: implement it
from a predicate that actually establishes write capability, or wait for the classifier repair — do
not read it off the classified set. If it is later scored, the weight remains policy; 5 to 10 keeps
it informative without making it a top-five rule on its first day.

**The methodological finding is the more durable one.** Three passes over the same question produced
per-section, per-interface, per-section — and the flip in the middle came from a population that was
*more* correct in one dimension (symbols, not files) and wrong in another (unfiltered, not exported).
`SectionShapeAnalyzer.FullServiceInterfaces` and the set `SurfaceScoreEngine` charges are not the same
set, and nothing in either output says so. Anyone measuring off the section-shape report should filter
by `IsExported` or cross-check against `--all` entries; better, the report should say which of the two
it is listing. That is a real gap in reforge's own output, not just a mistake in this document.

## Consequences for the 2026-08-15 spec

1. **Gate 2 is discharged for both #19 candidates as proposed**, and both answers are negative. The
   single-caller helper is rejected as a stock with evidence, and pointed at a delta form that has
   **not** itself been gated; feature envy is rejected as specified and refined into a form worth one
   more round. In both cases the successor needs its own Gate 2 read — a discharged gate that returns
   "no" does not pre-clear the thing it suggests instead.
2. **The per-section size denominator that spec asks for now exists.** It lists under the cleanup
   loop: *"Per-section density, not absolute points, must be derivable from the report… `typesAnalyzed`
   exists; a per-section size denominator does not."* It does now — #45 added a `metrics` block per
   section carrying `locProd`, `files`, `classes`, `interfaces`, `methods` and both complexity
   distributions, plus a solution rollup.
3. **The read-surface retirement argument strengthened**, from 7% of surface to 11.8%.
4. **Nothing here changes a weight.** All three signals are recommended against in their proposed
   form: two on precision, the third because its distribution on this corpus is 19 sections at one
   interface plus a single 16-interface outlier, so any per-interface weight would be fitted to that
   one section. Each has a stated next step; none of them is "pick a number now".
5. **Gate 2 needs a stated population, not just a stated predicate.** The spec requires measuring a
   candidate against a real corpus before weighting it. Three of the eleven defects review found in
   this round were predicates that were correct about *how* to count and wrong about *what* was in the
   population — `internal` methods, explicit interface implementations, non-exported interfaces. Two
   of those changed a number; one changed a recommendation and then changed it back. Worth adding to
   the gate: state the population and its exclusions alongside the predicate, and where the corpus
   offers a second route to the same count, take it.

## What was not measured, and why

- **A second corpus.** Every number is from Humans. Reforge itself is 1 section and 107 types with no
  DTOs and no repositories, so it cannot exercise these signals — it is a plumbing check, not a
  model check, as the 2026-08-15 spec already records.
- **Net-new single-caller helpers.** Requires two runs across a real change; the stock measurement
  above is what one run can establish, and it was enough to settle the question asked.
- **Whether the refined feature-envy hits are worth fixing.** Precision is measured; value is not.
  25 findings that an owner would decline to act on would be 25 findings too many.
