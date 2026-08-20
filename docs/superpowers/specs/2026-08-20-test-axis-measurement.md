# Test axis — measurement against a real test corpus

## Status

Measurement record (2026-08-20). Discharges the prerequisite issue #36 states for itself: *"None of
this is measurable from here. It needs a reading against a real corpus with real tests… before any
weight is picked."*

**Recommended against, in the shape #36 proposes.** The vacuous-test shapes it names do not occur in
this corpus. Of 4,759 test methods, the candidate population is **three or four assertions**, and on
reading, every one of them is a real regression test that fails on a change which compiles. Precision
of the rule as specified would be **0**.

Two rows of #36's own table are wrong as stated, and that is the transferable part of this document —
see "The criterion #36 needs".

One finding **unblocks #37** rather than blocking it: the test column that issue wants is a size
column, and a size column needs no test rules at all.

## Method

Grep over the test corpus, then a manual read of every hit outside the architecture-test files. No
Roslyn harness: the question is which *shapes* occur and whether their instances are vacuous, and the
second half is a reading, not a predicate. Where a count is syntactic it says so.

| | |
|---|---|
| Humans | `ff7881f`, cloned 2026-08-20 |
| reforge | `6d45daf` (branch `claude/project-next-steps-67z4gq`) |
| SDK | .NET 10.0.111 |
| test corpus | 47 test projects, 646 files, **123,458 non-blank lines**, **4,759 test methods** (4,569 `[HumansFact]`, 183 `[HumansTheory]`, 6 `[Fact]`, 1 `[Theory]`) |
| production corpus | 160,612 LOC / 1,805 files / 2,451 classes / 282 interfaces / 5,676 methods, from `surface-score`'s own metrics block |

## The population

Reflection is the tell #36 relies on, so start from every mention of a reflection API and narrow.

| | count |
|---|---:|
| Reflection rooted in `typeof(...)` — a compile-time constant | 223 |
| …of those, in a file named `*Architecture*` (60 such files) | 179 |
| …of those, everywhere else | **44** |
| Reflection rooted in a runtime instance's `.GetType()` | 27 |
| `Assert.NotNull(new X(...))` — #36's construction-only row | **0** |
| `typeof(X).Namespace` compared to a literal | **0** |

The 179 architecture-file hits are out of scope by #36's own text: the same call over a discovered set
is an architecture test, "which is a real thing and must not be charged."

The 27 instance-rooted hits are the largest false-positive class for any rule keyed on "a test used
reflection". They are controller tests reading an **anonymous type** returned by an action —
`value.GetType().GetProperty("title")!.GetValue(value).Should().Be("My Survey")`. The subject is a
runtime value; the assertion fails when the controller returns the wrong data. C# gives no other way
to assert on an anonymous return type.

## The 44 non-architecture hits, read one by one

| What it is | count | Vacuous? |
|---|---:|---|
| **Arrangement, not assertion** — `typeof(Survey).GetProperty(nameof(Survey.Id))!.SetValue(...)` to set an init-only entity id (10), `typeof(User).GetProperty("DisplayName")` in six section test harnesses (6) | 16 | No — not an assertion at all |
| **Discovered-set architecture tests living outside an `Architecture/` folder** — DI resolution sweeps, section activation, controller discovery, SDK containment, dependency-cycle scans, GDPR contributor discovery | ~20 | No — excluded by #36 |
| **Test infrastructure** — the test class fetching its own helper's `MethodInfo` to build an `ActionContext` | 2 | No |
| **Reflection as input to the code under test** — a `MethodInfo` handed to `RecurringJobExtensions.BuildCall<T>`, with the assertion on the built expression | 2 | No |
| **Attribute presence on a compile-time type** — `[Authorize]`, `[Route]` template, `[ValidateAntiForgeryToken]` per POST action | 3 | **No — see below** |
| **Constructor surface, stated negatively** — a dependency that must *not* be injected | 1 | **No — see below** |

### The attribute-presence tests are the opposite of vacuous

`CampaignControllerTests` asserts the controller carries a class-level `[Authorize]`, that its route
prefix is `Campaigns/Admin`, and that each POST action carries `[ValidateAntiForgeryToken]` and its
expected policy.

Delete any one of those attributes and the solution still compiles. The test fails, and what it caught
was an authorization hole. By #36's own decidability criterion — "passes for exactly as long as the
code compiles, and fails only when it is already failing to build" — these are not vacuous, they are
among the highest-value tests in the corpus. #36's table lists attribute presence as a vacuous shape;
on this corpus that row is simply wrong.

### The constructor-surface test is a pinned architectural decision

```csharp
var ctor = typeof(TicketNoShiftsAudience).GetConstructors().Single();
var paramTypes = ctor.GetParameters().Select(p => p.ParameterType).ToList();
paramTypes.Should().NotContain(typeof(IShiftSignups));
paramTypes.Should().NotContain(typeof(IShiftManagementServiceRead));
paramTypes.Should().Contain(typeof(IShiftView));
```

This is the single hit that matches #36's headline shape — signature reflection, parameter types
asserted against types — and it is a **negative** assertion. Reintroducing either dependency compiles
fine; DI would wire it; this test is the only thing that fails.

It is also pinning the exact fact reforge itself prices: a cross-section service dependency, charged
by `crossSectionFullService` (2,248 points on Humans, the #2 rule solution-wide). Charging the test
would penalize someone for pinning a decision the score pays them to make.

## The criterion #36 needs

The proposal keys on the API a test reached for. That does not survive contact with the corpus: the
same `GetParameters()` call is arrangement in one file, an architecture sweep in another, and a
dependency-removal guard in a third.

The criterion that does hold is one question, applied per assertion rather than per API:

> **Would this assertion fail on a change that compiles?**

If yes, it is a real test whatever API it used — attribute removal, a reintroduced constructor
parameter, a moved type, a renamed policy string. If no, it is vacuous. Restated for the shapes #36
lists: only assertions whose subject is a fact the **compiler would have rejected** are vacuous, and
this corpus contains none of them.

The corollary is that "signature reflection" and "attribute presence" cannot be rule rows at all. What
can: an assertion that a method *exists* with a signature the test itself references by `typeof` /
`nameof` in a way that would not compile if it were absent — the true tautology. Nothing here.

## Consequence for #37

#37 orders itself after this issue: *"#36 — the test axis needs its rules defined before there is
anything to put in that column."* That ordering only holds if the test column is a **score**. If it is
a size column — the treatment #45 already gives every section — it needs no rules and is computable
today:

- 123,458 test LOC against 160,612 production LOC — tests are **77% of production size**
- 47 test projects against 45 sections

The ratio #37 actually wants ("Shifts carries 3× the test mass of Camps") is a LOC ratio, and both
numbers exist. Attribution still needs resolving the way #37 says — a test project's section comes
from its non-test project references, not its own name, or `Humans.Shifts.Tests` and
`Humans.Shifts.IntegrationTests` land in different places.

So #37 is not blocked on a test axis. It is blocked on a decision about what the column measures.

## What was not measured, and why

- **A second corpus.** Every number is from Humans, and #36's motivating observation came from
  elsewhere ("I'm more lately seeing completely shit tests"). A zero here does not prove the shape is
  rare in general; it proves this codebase does not have it, which is what the gate asked.
- **Assertion-level counting by Roslyn.** The population is small enough to read. A harness would be
  needed to state precision on a corpus where the shape actually occurs.
- **Whether the tests that exist are any good.** Out of scope by construction: #36 is about tests that
  cannot fail, not tests that test little.
