# Pricing a section's contents

## Status

Design, not implemented. No weights committed; the measurements listed at the end come first, and
the weights land only after them. It supersedes nothing yet: the current `implementationShape`
rules stay in force until this replaces them.

Two prices per section:

- **Surface**: the cost of *depending* on the section, what its exported interface makes callers
  commit to. This exists. Every public method on an exported type is priced by behaviour through
  `MethodBehaviourMap`: `exportedReadMethod` 4, `exportedWriteMethod` 12, plus
  `exportedUnusedExternally` 2 when no other production assembly names it.
- **Contents**: the cost of *owning* the section, every line in it, public and private. This is the
  `implementationShape` axis, and it is what this document redesigns.

Humans figures below are marked *measured 2026-08, before #69-#72; rerun needed*. They illustrate
the shape of the problem; they are not current values.

## The problem with the contents axis

`implementationShape` is five threshold-gated rules: `cognitiveComplexity`, `largeClass`,
`mutationModeParameter`, `actionDispatcher`, `flagsControlFlow`. On Humans they totalled 2,530
against a surface of 16,689 (*measured 2026-08, before #69-#72; rerun needed*).

It is an exception report, not a census. `cognitiveComplexity` charges only the points over 15 on
a call path, and fired on 388 of 5,715 Humans methods (6.8%); `largeClass` fired on 16 of 2,474
classes (0.6%) (*measured 2026-08, before #69-#72; rerun needed*). The axis is silent on most
methods by design, so comparing it to the surface axis compares two instruments, one counting
every exported declaration and one counting outliers.

All the code counts, not just the worst offenders. The contents price should be a census.

## The two forces that have to balance

1. **Charge everything** without rewarding an optimizer for consolidating the corpus into fewer,
   larger methods to duck the count.
2. **Prefer 50 lines over 200** where both do the same job.

They pull opposite ways, and every dead end below satisfies one and breaks the other.

## Terms

All figures use a 400-raw-point method as the worked example and `f(n) = n·(1 + ln n)` as the size
curve.

### 1. Complexity, charged once per declaration

Every method declaration is charged for its own body, private included, from its own root. No
accessibility gate. The corpus total equals corpus complexity.

The curve is convex, so bigger costs superlinearly. Use `n·(1 + ln n)` rather than `n·ln n`: the
latter charges a complexity-2 method 1.39 points, less than its content, which undercounts the
small methods that are most of the corpus. The `+n` term is linear and cancels out of every
threshold below.

Charging from the declaration's own root is what makes splitting behave in both directions.
Cognitive complexity charges `1 + nestingDepth` per control structure and a helper's body starts
at depth 0, so extracting a nested block reduces the total, while extracting a flat block is
neutral.

### 2. Method count, flat and visibility-blind

Every declaration also pays a flat charge `m`, regardless of accessibility.

Without it, a convex curve collapses. `f(1) = 1`, so shattering 400 points into 400 one-point
methods costs 400 against `f(400) = 2,796`. This holds for any purely convex `f`:
`f(a+b) > f(a) + f(b)`, so every split pays and no split is ever the last.

With it, splitting into `k` equal pieces (equal is optimal for fixed `k`, by Jensen for convex `f`):

```
cost(k) = k·m + N·f(N/k)/(N/k)
d/dk    = m − N/k    →    k* = N/m    →    n* = m
```

**The optimal method size is exactly `m`, independent of `N`.** `m` pushes toward fewer, bigger
methods and convex `f` toward more, smaller ones; their balance names the target bite size in one
number: `m` is the method complexity we are willing to call manageable.

At `m = 10`, `N = 400`:

| split | method count | complexity | total |
|---|---|---|---|
| 1 × 400 | 10 | 2,796 | 2,806 |
| 4 × 100 | 40 | 2,242 | 2,282 |
| 10 × 40 | 100 | 1,876 | 1,976 |
| **40 × 10** | **400** | **1,321** | **1,721** |
| 100 × 4 | 1,000 | 954 | 1,954 |
| 400 × 1 | 4,000 | 400 | 4,400 |

Interior minimum at `n = m`. A piecewise floor (`cost(n) = n` below a threshold `t`, convex above)
is strictly worse: below `t` it has no gradient, so everything under `t` fragments for free.

**`m` must be visibility-blind.** Whatever the cheapest visibility costs sets the bite size, so
private at 1 point makes `k* = 400` and the optimum *is* 400 one-line private methods. The
deterrence floor is `400m + 400 > m + 2,796`, i.e. **`m > 6`**. Visibility is priced on the surface
axis and nowhere else.

### 3. Visibility belongs to the surface axis

Surface pricing by visibility is already done by behaviour (see Status): exported public methods
pay 4 or 12, internal and private pay nothing on surface. The contents axis must not add its own
visibility grading; its only per-declaration term is `m`, the same for every accessibility.

### 4. Depth factor, `(1 + 0.2·d)`

`d` is the **minimum** distance from the nearest public, protected, or internal entry point.
Minimum, not maximum: a helper reachable directly from an entry point is easy to find and should
score that way regardless of what else reaches it deeper down. Minimum also means adding a caller
never raises the cost of code that did not change.

Applied per declaration at 0.2 per level, against the same 400-point method:

| | complexity | count | total |
|---|---|---|---|
| one method, d=0 | 2,796 | 10 | 2,806 |
| 40 helpers, d=1 (×1.2) | 1,673 | 400 | 2,073 |
| 2-level tree, d≤2 (×1.4) | 2,038 | 480 | 2,518 |
| 3-level | | | worse than one method |

One level of extraction is clearly good, two roughly break-even, three loses: a public method
calling private helpers is good, a four-deep private chain is not.

The gaming move is a shallow entry point added to lower a deep helper's `d`. An internal shim
costs `m` plus its own small complexity and nothing on surface, and saves `0.2·f(c)` per level on
the helper, so at `m = 10` it pays for helpers above roughly complexity 14. A public shim on an
exported type also pays 4 or 12 on surface. Whether this matters is measurement 4 below.

### 5. Sharing factor, `(1 + 0.2·ln(callers))`

Complexity in widely called code is charged *more*, not less. `CallPathComplexity` already builds
solution-wide caller sets; the sole-caller test only asks whether the count is one.

| callers | shared | duplicated | ratio |
|---|---|---|---|
| 1 | 33 | 33 | n/a |
| 5 | 44 | 165 | 3.7:1 |
| 50 | 59 | 1,650 | 28:1 |

Sharing wins at every caller count because charging each declaration once already delivers that:
50 pasted blocks cost 50 times the complexity and 50 times the count, one shared method costs once.
Nothing needs dividing to make sharing attractive.

The premium on top is for blast radius. A method at complexity 60 called from 100 places is the
most important method in the corpus to keep simple.

## Dead ends, and why

### The fold cannot be the scoring basis

Current `cognitiveComplexity` scores on the fold: a method's call-path score is its own cognitive
complexity plus that of every helper of any accessibility, other than overrides and interface
implementations, whose sole caller it is within the same top-level type, transitively, and the
threshold of 15 applies to that sum. A group that calls single-caller methods in other types is
also charged what the type boundary kept under the threshold, at most one threshold (#76). This
document proposes moving the fold to a reporting view and scoring per declaration instead.

The fold exists to defend the threshold. Charge every method from zero and splitting is already
total-neutral without it. As a scoring basis under a census it fails three ways:

- **It kills the split incentive.** Fold the helpers back in and decomposition buys no complexity
  relief, so it only adds `m` per helper. The measure prefers one 400-point method over 40 named
  helpers.
- **Any positive depth multiplier reverses decomposition.** At 0.2 per level,
  `f(400 × 1.2) = 3,443`, plus 400 count, is 3,843 against 2,806 for the single method:
  decomposition is 37% worse. At `(1 + d)` it is 2.3 times worse. The rate is not the problem.
- **It cannot make sharing a win.** Folding stops at the second caller, so whether shared code is
  charged once or per caller is decided by a count of one, not by how widely it is used.

The fold survives as an **attribution and reporting** view ("what does it cost to read this entry
point") and stays out of the scored total. That also removes the sole-caller rule, and with it the
manufactured-second-caller escape fixtured on `cognitiveComplexity`, and the cross-type charge with
its one-threshold cap: a declaration costs the same in whichever type it sits.

### Dividing by caller count

`cost = C × 1.5 / k` fails twice:

- **The 1.5 flips decomposition negative.** The 40 helpers are each single-use, so every one pays
  1.5 times: `1,673 × 1.5 + 400 = 2,910` against 2,806. A single-use helper costs 15 points for 10
  points of content, worse than leaving it inline.
- **It makes the largest blast radius free.** Complexity 60 called from 100 places costs
  `60 × 1.5 / 100 = 0.9`, below a two-line private helper.

It also moves the wrong way: adding a call site anywhere lowers the score of untouched code.

### Purely superlinear curves

`n·log n` reaches 0 at `n = 1`, and log base is only a scale factor. Covered under term 2; this is
what `m` fixes.

## Open

### Read dispatchers

`actionDispatcher` and `mutationModeParameter` exempt read methods, so three exported reads
collapse into one enum-dispatched getter for free: `exportedReadMethod` drops from three charges to
one and nothing prices the dispatch (the `gate1-gameable` note on
`SampleSolution.Gate/Rules/exportedReadMethod.Before.cs`). The census closes the variant that keeps
the three bodies as private helpers, which now pays four `m` instead of three, but not the variant
that inlines the bodies into the switch, which lowers both axes; that still needs
`actionDispatcher` to price a read dispatcher whose arms share nothing.

### Duplication

A one-line extension method body is cognitive complexity 0. Pasting that line into 50 call sites
adds no branches and no declarations, so it costs **nothing** on complexity, count, or depth, while
extracting it costs `m` plus whatever surface the extension carries. Every term above prefers the
paste.

This is a blind spot, not a calibration error: complexity measures duplication at zero because
duplication does not branch. What makes 50 copies bad is 50 places to change, and nothing here
counts that. Closing it needs clone detection, which has to avoid flagging generated and
declarative bulk.

### Declarative bulk

A 241-line EF configuration at complexity 4 is long because the domain is wide, not because it is
hard. A length term is what closes the "50 lines beats 200" gap (200 lines of straight-line
assignment is complexity 0 and the same job as 50 lines with a loop is 2, so complexity alone prefers the
long version), but a naive length term charges a configuration for being a configuration. That is
why the current axis charges no length.

## Measure before assigning weights

Run each on Humans and on a sample of the wider sweep corpus, since Reforge is scored across
hundreds of unfamiliar solutions and one codebase's distribution is not a calibration.

1. **Declaration counts by visibility.** The `surface` census (`snapshot --by-assembly`) already
   gives exported and internal method counts per section; what is missing is private declarations,
   which `m` charges equally. Total declarations times `m` is the count term's size, and it sets
   how far every committed `implementationShape` baseline moves. That migration needs planning
   before the weights land.
2. **Raw unthresholded complexity distribution.** Decides whether the count term or the complexity
   term dominates the contents axis. If the count term is twice the complexity term, the axis says
   "too many methods" twice as loudly as "methods too complex", and that ordering should be a
   decision rather than an accident.
3. **Caller-count histogram over private methods.** If most have one or two callers, the sharing
   factor changes nothing and should not ship.
4. **Private closure sizes and depths per entry point.** Says whether the depth factor fires on
   anything real, and how many helpers sit above the shim break-even under term 4.

## Naming

Plain names only: complexity, method count, depth factor, sharing factor, duplication. The
fixture harness still carries an opaque name in code (`GateOneFixtureTests`,
`SampleSolution.Gate`, `gate1-gameable`); renaming it to *cheapest-fix fixtures* is an available
mechanical pass, not done here.
