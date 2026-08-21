# Pricing a section's contents

## Status

Design sketch (2026-08-21). Nothing implemented, no weights committed, four measurements
outstanding. Captured from a design conversation so next week starts from the conclusions rather
than re-deriving them.

Two prices per section:

- **Surface** — the cost of *depending* on the section. What its public interface makes callers
  commit to. This exists and works: ~3,500 published declarations at 1–8 points each, Humans 16,689.
- **Contents** — the cost of *owning* the section. Every line in it, public and private. This is
  what needs redesigning.

## The problem with what exists

The contents axis today is five threshold-gated rules — `cognitiveComplexity`, `largeClass`,
`mutationModeParameter`, `actionDispatcher`, `flagsControlFlow` — totalling 2,530 on Humans.

It is an exception report, not a census. `cognitiveComplexity` fires on 388 of 5,715 methods (6.8%).
`largeClass` fires on 16 of 2,474 classes (0.6%). The axis is silent on 93% of methods by design, so
16,689 against 2,530 is not a comparison between two weightings of the same thing — it is two
different instruments, one counting every published declaration and one counting outliers.

All the code counts, not just the worst offenders. The contents price should be a census.

## The two forces that have to balance

1. **Charge everything** without rewarding an optimizer for consolidating the corpus into fewer,
   larger methods to duck the count.
2. **Prefer 50 lines over 200** where both do the same job.

Those pull opposite ways, and every dead end below is a mechanism that satisfies one and breaks the
other.

## Terms

All figures use a 400-raw-point method as the worked example and `f(n) = n·(1 + ln n)` as the size
curve.

### 1. Complexity, charged once per declaration

Every method declaration is charged for its own body, private included, from its own root. No
accessibility gate. The corpus total equals corpus complexity.

The curve is convex, so bigger costs superlinearly. Use `n·(1 + ln n)` rather than `n·ln n`: the
latter charges a complexity-2 method 1.39 points, less than its actual content, which undercounts
the small methods that are most of the corpus. The `+n` term is linear and cancels out of every
threshold below, so it is free correctness.

Charging from the declaration's own root is what makes splitting behave in both directions. Sonar
charges `1 + nestingDepth` per control structure and a helper's body starts at depth 0, so
extracting a nested block genuinely reduces the total, while extracting a flat block is neutral.

### 2. Method count, flat and visibility-blind

Every declaration also pays a flat charge `m`, regardless of accessibility.

Without it, a convex curve collapses. `f(1) = 1`, so shattering 400 points into 400 one-point
methods costs 400 against `f(400) = 2,796` — and it is not specific to this curve: *any* purely
convex `f` satisfies `f(a+b) > f(a) + f(b)`, so every split pays and no split is ever the last.

With it, splitting into `k` equal pieces (equal is optimal for fixed `k` — Jensen runs that way for
convex `f`):

```
cost(k) = k·m + N·f(N/k)/(N/k)
d/dk    = m − N/k    →    k* = N/m    →    n* = m
```

**The optimal method size is exactly `m`, independent of `N`.** Two opposing pressures — `m` toward
fewer and bigger, convex `f` toward more and smaller — and their ratio names the target bite size in
one number: `m` is the method complexity we are willing to call manageable.

At `m = 10`, `N = 400`:

| split | method count | complexity | total |
|---|---|---|---|
| 1 × 400 | 10 | 2,796 | 2,806 |
| 4 × 100 | 40 | 2,242 | 2,282 |
| 10 × 40 | 100 | 1,876 | 1,976 |
| **40 × 10** | **400** | **1,321** | **1,721** |
| 100 × 4 | 1,000 | 954 | 1,954 |
| 400 × 1 | 4,000 | 400 | 4,400 |

Interior minimum at `n = m`. No piecewise floor needed — an earlier sketch proposed
`cost(n) = n` below a threshold `t` and a convex surcharge above, which is strictly worse: below `t`
it has no gradient, so everything under `t` fragments for free.

`m` must be visibility-blind. Whatever the cheapest visibility costs is what sets the bite size, so
private at 1 point makes `k* = N/1 = 400` and the optimum *is* 400 one-line private methods. The
deterrence floor is `400m + 400 > m + 2,796`, i.e. **`m > 6`**.

### 3. Surface count, graded by visibility

|  | public | protected | internal | private |
|---|---|---|---|---|
| surface | 10 | 10 | 5 | 0 |
| contents (`m`) | 10 | 10 | 10 | 10 |

Private is 0 on surface — it costs nothing to depend on. Protected sits with public: it is contract
for subclasses. Composed, a public method costs 20, internal 15, private 10, so "8 public methods
beats 99 public methods at the same complexity" is priced on the surface axis, where it belongs,
while the contents axis keeps its escape-proof floor of 10.

This term should **replace** the classifier-gated per-method surface rules. Measured on Humans,
`applicationServiceMethod` fires on 2 methods for 10 points and `repositoryImplementationMethod` on
0 for 0, across 3,621 types; a public method is only priced when it sits on an interface
(`fullServiceInterfaceMethod`, 3,480 points over ~435 methods). A flat visibility-graded count has
no classifier to admit too few types, no double-charge suppression, and no interface requirement.

### 4. Depth factor, `(1 + 0.2·d)`

`d` is the **minimum** distance from the nearest public, protected, or internal entry point.
Minimum, not maximum: a helper reachable directly from a public method is easy to find and should
score that way regardless of what else reaches it deeper down. Minimum also removes the instability
where adding an unrelated caller raises the cost of code that did not change — a new shallower
caller only ever lowers it.

Applied per declaration at 0.2/level, against the same 400-point method:

| | complexity | count | total |
|---|---|---|---|
| one method, d=0 | 2,796 | 10 | 2,806 |
| 40 helpers, d=1 (×1.2) | 1,673 | 400 | 2,073 |
| 2-level tree, d≤2 (×1.4) | 2,038 | 480 | 2,518 |
| 3-level | | | worse than one method |

One level of extraction is clearly good, two roughly break-even, three loses. That is the intended
calibration: a public method calling private helpers is good, a four-deep private chain is not.

The gaming angle is self-limiting: adding a shallow public caller to drop a deep helper's `d` costs
10 surface + 10 count and worsens the surface axis, so the two-scalar Pareto view blocks it.

### 5. Sharing factor, `(1 + 0.2·ln(callers))`

Complexity in widely-called code is charged *more*, not less. Caller sets are already built
solution-wide — `CallPathComplexity.Callers` — and the sole-caller test discards the count.

| callers | shared | duplicated | ratio |
|---|---|---|---|
| 1 | 33 | 33 | — |
| 5 | 44 | 165 | 3.7:1 |
| 50 | 59 | 1,650 | 28:1 |

Sharing wins by a wide margin at every caller count, because charging each declaration once already
delivers that — 50 copy-pasted blocks cost 50× the complexity and 50× the count, one shared method
costs 1× and 1. Nothing needs dividing to make sharing attractive.

The premium on top is for blast radius. A method at complexity 60 called from 100 places is the most
important method in the corpus to keep simple; 100 things break when its behaviour changes.

## Dead ends, and why

### The fold cannot be the scoring basis

`effCognitive(M) = CC(M) + Σ effCognitive(H)` over private helpers `H` that `M` invokes. It exists
only to defend the current threshold — charge every method from zero and splitting is already
total-neutral without it. As a scoring basis it fails four ways:

- **It kills the split incentive.** Fold the helpers back in and decomposition buys no complexity
  relief at all, so it purely costs the per-method charge. The measure prefers one 400-point method
  over 40 named helpers.
- **Any positive depth multiplier reverses decomposition.** At 0.2/level,
  `f(400 × 1.2) = 3,443 + 400 = 3,843` against 2,806 for the single method — decomposition is 37%
  worse. At `(1 + d)` it is 2.3× worse. The rate is not the problem.
- **It makes sharing neutral instead of a 50:1 win.** A helper called from 50 places is charged 50
  times, so the only way to make shared code cheap is to delete it.
- **Promoting a private helper to internal becomes a discount** — it leaves every caller's fold and
  is charged once instead of 50 times. Surface rises 5, contents drops a lot; only the two-scalar
  Pareto view stops that reading as a win.

The fold survives as an **attribution and reporting** view — "what does it cost to read this entry
point" — and stays out of the scored total. That also dissolves the sole-caller restriction and with
it the manufactured-second-caller escape.

### Dividing by caller count

`cost = C × 1.5 / k` was the sketch. It fails twice:

- **The 1.5 flips decomposition negative.** The 40 helpers are each single-use, so every one pays
  1.5×: `1,673 × 1.5 + 400 = 2,910` against 2,806. It charges a single-use helper 15 points for 10
  points of content, worse than leaving it inline.
- **It makes the largest blast radius free.** Complexity 60 called from 100 places costs
  `60 × 1.5 / 100 = 0.9` — below a two-line private helper.

It is also destabilising in the wrong direction: adding a call site anywhere lowers the score of
untouched code, so the number improves by calling things more.

### Purely superlinear curves

`n·log n` reaches 0 at `n = 1`, and log base is only a scale factor. Covered under term 2 — this is
what `m` exists to fix.

## Open: duplication

A one-line extension method body is cognitive complexity 0. Copy-pasting that line into 50 call
sites adds no branches and no method declarations, so it costs **nothing** on complexity, count, or
depth, while extracting it costs 10 surface + 10 count. Every term above prefers the copy-paste.

This is a blind spot, not a calibration error: complexity measures duplication at zero because
duplication does not branch. What makes 50 copies bad is 50 places to change, and nothing here
counts that. Closing it needs clone detection, which brings its own question — whether it can avoid
flagging generated and declarative bulk.

Related and also open: **declarative bulk**. A 241-line EF configuration at complexity 4 is long
because the domain is wide, not because it is hard. A LOC term is what closes the "50 lines beats
200" gap — 200 lines of straight-line assignment is complexity 0, and rewriting it as 50 lines with
a loop is complexity 2, so complexity alone prefers the long version — but a naive LOC term charges
the EF configuration for being a configuration.

## Measure before assigning weights

Four numbers, none of them known:

1. **Method counts by visibility across Humans.** 10/5/0 over ~5,715 methods likely puts the surface
   axis in the 30–40k range against 16,689 today. That is a rescale, not an increment: every
   committed baseline moves, and the migration needs planning before the weights land.
2. **Raw unthresholded complexity distribution.** Decides whether the count term or the complexity
   term dominates the contents axis. At `m = 10` the count alone is 57,150 points; if raw complexity
   totals ~25–30k, the axis says "too many methods" twice as loudly as "methods too complex," and
   that ordering should be a decision rather than an accident.
3. **Caller-count histogram over private methods.** If most have one or two callers, the sharing
   factor changes nothing and should not ship.
4. **Private closure sizes and depths per entry point.** Says whether the depth factor fires on
   anything real.

## Naming

Plain names only. The terms above are complexity, method count, surface count, depth factor,
sharing factor, duplication. Two labels used in earlier discussion — "Gate 1" for the cheapest-fix
fixture harness, and an invented second gate for "measure it before weighting it" — conveyed
nothing and are dropped. The harness itself (`GateOneFixtureTests`, `SampleSolution.Gate`,
`gate1-gameable`) still carries the opaque name in code; renaming it to *cheapest-fix fixtures* is
an available mechanical pass, not done here.
