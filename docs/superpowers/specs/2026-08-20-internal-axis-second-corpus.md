# Internal-axis signals — the second corpus, and the delta form

## Status

Measurement record (2026-08-20). Addendum to `2026-08-19-internal-axis-signal-measurements.md`,
closing the two gaps that document names in "What was not measured, and why":

> - **A second corpus.** Every number is from Humans. Reforge itself is 1 section and 107 types with
>   no DTOs and no repositories, so it cannot exercise these signals […]
> - **Net-new single-caller helpers.** Requires two runs across a real change […]

Both are now measured. Three findings:

1. **The dismissal of reforge as a second corpus is half wrong.** It cannot exercise feature envy —
   that much holds. It exercises single-caller helpers *harder than Humans does*: 43% of method LOC
   against Humans' 13%.
2. **The delta form of Signal A is rejected too.** Newly-added private methods are single-caller at
   73–81%, which is the stock rate. A rule charging net-new single-caller helpers charges three of
   every four private methods a change adds — a size rule with extra steps.
3. **One form has never been measured and is not rejected:** the per-section *share*. It spans 0.5%
   to 37.3% across 42 Humans sections, a 70× range. Recorded as a candidate, not recommended — see
   the caveat, which is that its top hit is a section where the shape is correct style.

Nothing here proposes a weight.

## Method

Same treatment as the harness the 2026-08-19 spec describes: a throwaway Roslyn console project
(~250 lines, not committed), one document walk per non-test project, excluding `obj/`, `bin/`,
`Migrations/`, `*.g.cs`, `*.Designer.cs`, `*.generated.cs`.

| | |
|---|---|
| Humans | `283510e`, 67 projects, 7,601 methods, 112,125 method LOC, 1,388 private methods |
| Reforge | `2dec9af` and five earlier commits back to `ae6cee0` (2026-05-30) |
| SDK | .NET 10.0.111 |
| Humans score | `surfaceTotal` 16,689, `internalComplexityTotal` 3,038, combined 19,727, `degraded: false` |

The Signal A predicate here is simpler than the 2026-08-19 one — `private`, has a body, distinct
enclosing members as callers — and does **not** exclude explicit interface implementations or
`nameof` mentions. It reproduces that document's stock base rate to within 0.3 points (59.5% here vs
59.2% there), which is the check that matters: the two harnesses agree on the same corpus, so the
numbers below can be read against that document's.

Humans is a shallow clone (4 commits), so its history is not measurable from here. Reforge's is —
66 commits over 82 days — and it is the population #19 is actually about, being LLM-written
end to end.

## Gap 1 — the second corpus

| | private | single-caller | share | helper LOC | method LOC | LOC share |
|---|---:|---:|---:|---:|---:|---:|
| Humans (human + LLM) | 1,388 | 826 | 59% | 15,679 | 112,125 | **13%** |
| Reforge (LLM end to end) | 306 | 246 | 80% | 5,492 | 12,714 | **43%** |

Reforge carries 3.3× Humans' single-caller-helper LOC share. The 2026-08-19 spec's reason for
setting reforge aside — 1 section, no DTOs, no repositories — is a statement about the *surface*
axis. Signal A needs none of those things; it needs private methods, and reforge has 306.

That the two corpora differ this much on a signal neither was tuned against is the first evidence
that it measures something about *how the code was written* rather than how much of it there is.

## Gap 2 — the delta form

The 2026-08-19 spec confirms Signal A "as a delta signal" and is explicit that the successor was not
itself measured. Six commits across reforge's history:

| commit | date | methods | method LOC | private | single-caller | share | LOC share |
|---|---|---:|---:|---:|---:|---:|---:|
| `ae6cee0` | 05-30 | 306 | 9,069 | 205 | 169 | 82% | 44% |
| `cc48bbf` | 06-06 | 329 | 9,609 | 221 | 182 | 82% | 41% |
| `c8b49b5` | 08-13 | 337 | 9,782 | 227 | 188 | 82% | 42% |
| `324f495` | 08-15 | 369 | 10,501 | 237 | 196 | 82% | 39% |
| `0cc1128` | 08-19 | 389 | 10,953 | 251 | 206 | 82% | 39% |
| `2dec9af` | 08-20 | 458 | 12,714 | 306 | 246 | 80% | 43% |

The share does not move. Over 82 days and +101 private methods it sits at 80–82% throughout.

The marginal rate — of the private methods a stretch of history *added*, how many are single-caller —
is the number the delta rule would charge:

| span | private added | single-caller added | marginal rate |
|---|---:|---:|---:|
| `ae6cee0` → `cc48bbf` | +16 | +13 | 81% |
| `0cc1128` → `2dec9af` | +55 | +40 | 73% |
| `ae6cee0` → `2dec9af` | +101 | +77 | 76% |

**The marginal rate is the stock rate.** A delta-native rule charging net-new single-caller helpers
would fire on roughly three of every four private methods a change adds, in proportion to how much
the change adds — which is the defining property of the size rules #19 exists to get away from.

This does not disprove that linter-driven extraction is *distinguishable* from intended
decomposition. It shows that the population a delta rule would charge is not predominantly the
former, on the one corpus where the whole history is LLM-written. The 2026-08-19 audit already
suspected the distinction "may not be decidable from structure at all"; this is the quantitative
version of that suspicion.

### Consequence for the size rules

#19's premise is that `longMethod` and `cognitiveComplexity` are gameable by extraction and so
should be reweighted or retired, with a single-caller-helper rule as the counterweight. With both
the stock and delta forms of the counterweight rejected, the premise needs restating rather than
implementing:

- Extraction satisfies `longMethod`. **Inlining** satisfies a single-caller-helper rule. Neither is
  independently safe.
- The only edit that satisfies both at once is giving a helper a second real caller, or deleting
  duplicated logic. That is reuse, which is what `CLAUDE.md` says the tool exists to encourage.
- So the pairing is sound in principle and the measurement says nothing against it. What the
  measurement says is that **the counterweight cannot be a per-helper charge in either form** — the
  base rate is too high for a stock charge and the marginal rate is too high for a delta charge.

## The form nobody has measured — per-section share

Single-caller helper LOC as a percentage of the section's production LOC, Humans, 42 sections:

| | section | share |
|---|---|---:|
| top | Analyzers | 37.3% |
| | Stripe | 33.9% |
| | TicketTailor | 26.4% |
| | Search | 25.5% |
| | Monitor | 22.7% |
| | Agent | 20.4% |
| bottom | Auth | 2.6% |
| | Governance | 1.0% |
| | Email | 0.5% |

A 70× spread, and reforge at 43% sits above every Humans section. A rule of this shape charges a
section for a property of its *shape* rather than charging each helper, so extraction raises the
charge instead of lowering it — the anti-gaming direction #19's Gate 1 asks for.

**Not recommended, and specifically not on the strength of that spread.** The top hit is
`Humans.Analyzers`, where decomposing into many small single-caller rule methods is the correct style
for the domain — one method per diagnostic, called once from a registration. If the loudest hit is
correct code, the signal may be measuring "small-method-shaped" rather than "fragmented", which is
the same failure the duplication rule died of. Settling that needs a manual read of the top three
sections' helpers, which this round did not do.

## Incidental

**Uncalled private methods: 32 on Humans, 245 LOC.** Dead code, found for free by the same pass.
Too small and too unambiguous to score — a `reforge dead-private` diagnostic at most, and only if
someone asks for it.

## The size rules — measure the call path, not the declaration

#19's principal complaint, stated plainly by the owner: an 800-line method `Foo` scores for being
long; split into `Foo1`..`Foo4` it scores less or nothing, and the code is worse. The counterweight
this issue proposes (charge the helpers) is dead in both forms above. The alternative is to change
the **unit of measure**: a method with exactly one caller is not a method, it is part of its caller.

### The predicate

`effLoc(M) = LOC(M) + Σ effLoc(H)` over every private helper `H` that `M` **invokes** and whose
caller set is exactly `{M}`, computed transitively with a cycle guard. `effCognitive` is the same
sum over Sonar cognitive complexity, which composes because it is itself a sum of local increments.

Two details are load-bearing:

- **Fold only into the sole caller.** A helper with two callers is shared code and folding it into
  both would double-count it. It also stops folding the moment it gains a second caller, which is
  the incentive: give a helper a real second use and the caller's charge falls. That is reuse, which
  is what the tool exists to encourage.
- **Follow invocations, not method groups.** A method handed to something else as a delegate is a
  separate entry point. Folding registered callbacks into a Roslyn analyzer's `Initialize` read
  15 six-line registration methods as 55–129 line bodies. Excluding method groups removed all 15
  and introduced no new hits.

### Result

Both corpora, with reforge's own `NonBlankLines` and `CognitiveDetail` so the syntactic column
reproduces the tool exactly (`longMethod` 1,387 and `cognitiveComplexity` 740 on Humans — the same
figures `surface-score` reports):

| | Humans syntactic | Humans folded | Reforge syntactic | Reforge folded |
|---|---:|---:|---:|---:|
| `longMethod` points | 1,387 | **3,440** (2.5×) | 498 | **1,444** (2.9×) |
| `cognitiveComplexity` points | 740 | **1,595** (2.2×) | 1,298 | **2,485** (1.9×) |
| methods over 40 LOC | 350 | 483 | 80 | 102 |
| methods over CC 15 | 83 | 158 | 70 | 93 |
| methods charged only after folding | — | 168 | — | 29 |

A folded-away helper charges nothing of its own — its lines are billed once, to the root that owns
the call path. Summing every declaration's folded figure instead would double-count the helpers
(3,953 / 1,962 on Humans); the table above bills roots only, which is what an implementation would
do.

Reforge's `longMethod` multiplier is 4.0× against Humans' 2.9×, consistent with its 43%-vs-13%
single-caller-helper share: the LLM-written corpus hides proportionally more behind helper
boundaries.

### What the fold finds that the declaration does not

The 168 newly-charged methods are the population #19 is about. The clearest cases:

| declared | call path | declared CC | call-path CC | method |
|---:|---:|---:|---:|---|
| 6 | 182 | 0 | 30 | `StoreWebhookRegistrationService.StartAsync` |
| 12 | 186 | 1 | 32 | `ShiftManagementService.GetDashboardOverviewAsync` |
| 23 | 234 | 4 | 25 | `ExternalLoginService.CompleteExternalLoginAsync` |
| 42 | 239 | 2 | 40 | `WorkloadService.GetForActiveEventAsync` |
| 33 | 196 | 1 | 36 | `VolunteerTrackingService.GetTrackingDataAsync` |

A six-line method carrying 182 lines of call path charges **nothing** today.

Folding also **reorders** the findings rather than merely inflating them: the top-20 by points under
the two measures overlap by 7 of 20. The syntactic measure does not point at the same methods.

### Gate 1

The cheapest edit that satisfies the folded measure is to remove logic from the call path, to give a
single-caller helper a second real caller, or to take a block out of a nest. Splitting a method into
single-caller parts **at the same nesting depth leaves the number unchanged** — the property the
syntactic measure lacks and the reason this succeeds where the per-helper counterweight failed.

One qualification, found by scoring this change against itself. Cognitive complexity charges
`1 + nestingDepth` per control structure and a helper's body is charged from its own root, so an
extraction that *flattens* still pays, in proportion to the depth it removes. `CallPathComplexity`
itself is the worked example: one four-deep loop nest read CC 104 for 89 points, and the same logic
as four methods at depth 2 reads CC 65 for 50. The four helpers are private and single-caller, so
they fold straight back in — none of the 39-point drop came from the split, all of it came from the
two levels of nesting the split removed. That is not the fake split; it is the edit Sonar's nesting
penalty exists to reward. But "unchanged by construction" is true only of a split that keeps the
depth.

### The calibration, and what it argues for

Folding both size rules is not weight-neutral: Humans' internal axis goes 3,038 → 5,946 and the
combined total 19,727 → 22,635, taking internal from 15% of the score to 26%.

But the fold also settles the LOC-versus-complexity question, because the two rules stop being
independent once they measure the same call path. Of the 388 Humans methods that charge under the
folded measure, 274 charge on **lines only** — 1,166 points on call paths whose cognitive complexity
is under threshold:

| points | folded LOC | folded CC | method |
|---:|---:|---:|---|
| 55 | 241 | 4 | `TeamConfiguration.Configure` |
| 23 | 171 | 9 | `PreMigrationSnapshot.EnsureCapturedAsync` |
| 22 | 163 | 14 | `ProfileController.BuildEmailsViewModelAsync` |
| 21 | 155 | 8 | `GoogleWorkspaceSyncService.GetAllDomainGroupsAsync` |
| 21 | 151 | 6 | `UserService.ContributeForUserAsync` |

The top hit is an EF entity configuration: 241 declarative lines, four branches. These are long
because the domain is wide, not because they are hard, and there is no edit that shortens them
except moving lines somewhere else — the fake split again, one level up. Only 8 methods charge on
complexity alone, so cognitive complexity is very nearly a subset of what LOC charges, minus the
declarative bulk.

So the recommendation is not "fold both" but **fold, and let folded cognitive complexity be the
only size charge** — retire `longMethod`. That is weight-neutral without touching any other rule:

| | today | fold both | fold, retire `longMethod` |
|---|---:|---:|---:|
| Humans internal axis | 3,038 | 5,946 | **2,506** |
| combined total | 19,727 | 22,635 | **19,195** |
| internal share | 15% | 26% | **13%** |

The residual risk is a 300-line straight-line method charging zero. If that shows up in practice the
answer is a single high-threshold folded-LOC backstop (one charge above ~180 call-path lines), not
the graduated per-10-line curve — a curve is what makes the split profitable in the first place.

**Shipped.** `CallPathComplexity` resolves the fold; `longMethod` is gone. Scored on Humans
(`283510e`, `Humans.Score.slnf`, not degraded): `cognitiveComplexity` 1,619, internal axis
**2,530**, combined **19,219**, surface unchanged at 16,689 — within 1% of the projection above.
The engine differs from the probe by excluding DTO data carriers and generated declarations, which
is the whole of the gap.

The escape the fold leaves is a *manufactured second call site*: route an existing method through
the helper and it stops folding, both readings fall under the threshold, and the rule stops. It is
fixtured as `cognitiveComplexity.Before.cs` with the `gate1-gameable` marker, so the harness asserts
that it pays — the finding is recorded rather than implied. It is narrower than what it replaces: the
split an agent reaches for first is now free in both directions, and escaping takes a call site a
reader can see was added for the score. Closing it means not gating on caller count, and both
alternatives measured worse (a per-helper charge is a size rule at a 59–80% base rate; folding a
shared helper into each caller counts it twice).

Secondary consequence: the metric becomes non-local — a method's charge depends on its callees, so
editing a helper moves its caller's score. `largeClass` is unaffected (a class already contains its
helpers, which is why it is the one size rule the fake split never fooled).

## The read-surface retirement, rejected

#19's "related but separable" section proposes retiring the read-surface rules. Measured on
`283510e` they are **2,737 points, 13.9% of 19,727** — not the 1,230 / 7% the issue states, and
`readServiceInterfaceMethod` alone is the fifth-largest rule in the tool.

| rule | points |
|---|---:|
| `readServiceInterfaceMethod` | 1,386 |
| `readSurfaceProjectionMethod` | 748 |
| `crossSectionReadInterface` | 606 |
| `writeCapableInterfaceUsedReadOnly` | 72 |
| `canonicalReadDtoReturn` | −75 |

Rejected, and not on magnitude. The argument for cutting them — assemblies carry the boundary now,
so charging for a read surface charges the preferred shape — conflates *which interface setup is
preferred* with *how much surface exists*. Read surface is still surface: a read interface with 40
methods publishes 40 methods, and read-only says nothing about whether all 40 were required. Zeroing
the read path stops measuring it, and what is not measured cannot be pushed anywhere.

Least privilege is carried by the **differential** — read 6/method against full 8, cross-section
read 2 against full 8 — not by making one side free. That is the shipped design.

The issue's stated reason was config surface and special-case code rather than points. The config
half is already discharged by #60: four keys remain across all five rules. The 882 lines in
`SectionShapeAnalyzer` and `CanonicalReadDtos` are implementation complexity, which is a refactor
under a score-must-not-move gate, not a retirement.

## `largeClass` — keep at weight 1

The last of #19's three size rules without a recorded decision. Measured on Humans `283510e`,
`Humans.Score.slnf`, `degraded: false`.

**Population.** `largeClass` charges only application services, repository implementations,
controllers and background jobs (`IsSizeTrackedClass`), summed over handwritten declarations, at +10
per 250 nonblank LOC above 750 and +25 above 1,500. It charges **16 classes, 455 points, 8 of 44
sections**:

| class | LOC | pts | | class | LOC | pts |
|---|---:|---:|---|---|---:|---:|
| `TeamService` | 2,211 | 75 | | `ExpenseReportService` | 1,167 | 10 |
| `ProfileController` | 2,025 | 75 | | `CampController` | 1,160 | 10 |
| `ShiftManagementService` | 1,851 | 65 | | `ShiftSignupService` | 1,149 | 10 |
| `GoogleWorkspaceSyncService` | 1,746 | 55 | | `TeamRepository` | 1,108 | 10 |
| `UserRepository` | 1,589 | 55 | | `BudgetRepository` | 1,096 | 10 |
| `Store.Service` | 1,374 | 20 | | `UserService` | 1,069 | 10 |
| `CampService` | 1,304 | 20 | | `TeamAdminController` | 1,062 | 10 |
| | | | | `UserEmailService` | 1,059 | 10 |
| | | | | `CachingTeamService` | 1,035 | 10 |

The four classes over 1,700 lines carry 270 of the 455 points; nine classes pay the minimum 10.
Not a broad tax.

**Gate 1.** Three edits satisfy it, and only one is cheap:

- *Mark it `partial` and split the files.* **Already closed.** Partial declarations sum, per
  declaration rather than from the primary file, and `SectionMetrics` measures the same way.
- *Split into a second named type.* The open escape, and it is not a relabelling. `TeamService` has
  99 methods and 47 injected dependencies; a second type has to take a partition of those 47 through
  its own constructor, and cannot reach the original's private state. Getting under 750 from 2,211
  is a three-way split of that dependency set.
- *Move methods to the section that owns their data.* The desired outcome, and `misplaced` already
  names the candidates.

**Why it does not share `longMethod`'s fate.** What killed `longMethod` was a gaming boundary any
extraction could cross: a 40-line threshold on a graduated curve, so 20×40 LOC scored 0 where
800 scored 111. `largeClass` has no such boundary — one threshold at 750, a coarse step above it,
and a population where crossing it means partitioning a constructor.

**Threshold-hugging check.** If the threshold were being shaved to, sizes would bunch just under
750. Measured in the rule's own unit — nonblank LOC per class, summed across partials — by a
separate scanner validated against the 15 figures the rule itself reports (6 exact, the rest
undercounting by 1–10 lines on leading attributes, so read the numbers as ±10):

| band | names |
|---|---|
| 620–749 | 11 — `EventsController` 732, `Tickets.Repository` 718, `EventService` 707, … `TicketTransferService` 647 |
| ≥750 | 25, of which 6 within 90 lines of the threshold — `AgentService` 755, `ExpensesController` 756, `BudgetService` 772, `IssuesService` 783, `TicketQueryService` 795, `TicketRepository` 840 |

Ordinary density on both sides and no bunching: the closest below is 18 lines under, and six classes
sit just over and pay. The scanner's `*Service`/`*Controller`/`*Repository`/`*Job`/`*Seeder` name
shape is **wider** than `IsSizeTrackedClass`, which is why it finds 25 at ≥750 where the rule charges
16 — the check is about the distribution, not the charge.

**Decision: keep, weight 1, unchanged.** No Gate 1 fixture: the threshold is 750 LOC, so a
`Before`/`CheapestFix` pair costs roughly 1,600 lines of synthetic class to demonstrate one 10-point
charge. The exemption in `GateOneFixtureTests` records that as the reason, replacing the previous
one, which promised a cohesion-based replacement this measurement does not recommend.

## The axis is `implementationShape`

#19's secondary complaint, discharged. `internalComplexity` never had an `internal`-accessibility
gate — the axis is a fixed rule-name partition, so public code scores on it too — and the README
reinforced the wrong reading by describing it two paragraphs after the effectively-public surface
gate.

Renamed throughout: `implementationShapeTotal` in JSON, `implementationShape=` in compact,
**Implementation shape** in markdown, `SurfaceScoreRuleGroups.ImplementationShape`. Baselines are
committed files that outlive the build that wrote them, so `ReadScope` accepts either key spelling
and `self-score-report.py` does the same. Score-neutral on both corpora: Humans 16,689 / 2,530 /
19,219 and reforge surface 53, both unchanged.

## What is still not measured

- Whether the per-section share separates fragmentation from correct small-method style. Needs the
  manual read named above.
- Feature envy on a second corpus. Reforge cannot supply it: 0 DTO anchors, and the 2026-08-19
  refinement needs entity/DTO classification the tool does not have.
- Whether any of this changes with a corpus that has been through a scored refactor. Nothing has
  been refactored *to satisfy reforge* yet, so every number here is the pre-scoring baseline. The
  fragmentation #19 fears is a predicted response to the tool, and the response cannot be measured
  before the tool is used that way. `self-score.yml` makes it observable from here on.
