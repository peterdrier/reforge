# Effective Public Surface - Design Spec

## Status

Approved (2026-08-13). Targets reforge v0.24.0 (minor, scoring-semantics change). Fixes issue #13.

## Context

Since #10, a section IS an assembly. That turns "public surface" from a judgement call into a
compiler-enforced fact: it is what the assembly exports. Everything `internal` or `private` is
implementation — no other section can call it, so no consumer can be broken by changing it.

The corpus did not reflect that. `SolutionClassifier.ClassifyAsync` admits every type that is not
`Private`, so internal types entered the corpus and scored as surface. The scoring passes then
checked only **declared member** accessibility (`m.DeclaredAccessibility != Accessibility.Public`,
~10 sites) and nobody computed **effective** accessibility. Consequences:

- A `public` method on an `internal` class scored full surface points.
- A `public` type nested inside an `internal` type likewise.
- Rules charging for durable API — `repositoryInterfaceMethod`, `fullServiceInterfaceMethod`,
  `applicationServiceMethod`, `publicDtoType`, `dtoScalarProperty`, `newRepositoryInterface` — all
  charged for API that is not published.

The perverse incentive: the cheapest way to "improve" the score was to make things internal without
changing anything a consumer sees, while genuinely encapsulating a type — a real decoupling win —
was invisible.

## Goal

Score only what crosses the assembly boundary. Internal and private code keeps counting on the
**sizing / internal-complexity axis** (`longMethod`, `largeClass`, `cognitiveComplexity`, dispatcher
rules), because how big the implementation is still matters — just not as *surface*.

## Definition

`SurfaceVisibility.IsExported(ISymbol)` walks a symbol and every containing type out to the
outermost declaration; the symbol is exported only if every step is `public`.

Two deliberate exclusions:

- **`protected` is not exported.** It is reachable only by deriving, and every scoring pass has
  always required `Accessibility.Public` on members. Admitting protected types while still skipping
  protected members would be incoherent.
- **`InternalsVisibleTo` is ignored.** A test project or analyzer seeing internals does not make
  them product surface — nothing ships against them.

## The split: declarations are gated, uses are not

The issue's acceptance criterion reads "surface-axis rules score only effectively-public members of
effectively-public types". Applied literally to every surface-axis rule that would also silence the
cross-section coupling rules — and that is wrong, for the reason the issue itself exists.

Surface is *what another section can call*. A cross-section dependency is exactly that: the
consumer's assembly references the other section's assembly and calls across the boundary. Marking
the **consumer** `internal` does not remove the reference and does not remove the call. Gating those
rules would have made coupling free, recreating the same gaming vector one rule family over.

So the gate follows what a rule charges for:

**Gated — the rule charges for a declaration's published shape:**
`publicDtoType`, `dtoScalarProperty`, `dtoCollectionProperty`, `dtoNestedProperty`,
`readServiceInterfaceMethod`, `fullServiceInterfaceMethod`, `repositoryInterfaceMethod`,
`repositoryImplementationMethod`, `newRepositoryInterface`, `newRepositoryImplementation`,
`applicationServiceMethod`, `controllerAction`, `backgroundJob`, `methodParameterOverflow`,
`booleanParameter`, `tupleReturn`, `optionsBag`, `dashboardAdminPageName`,
`oneImplementationInterface`, `canonicalReadDtoReturn`, `methodReturnsEntityAcrossSection`,
`publicInputWithHiddenState`, `parameterBagInput`, `inlineParameterObjectConstruction`,
`readSurfaceProjectionMethod`.

**Not gated — the rule charges for a use, which crosses the boundary regardless:**
`sameSectionReadService`, `crossSectionReadInterface`, `crossSectionFullService`,
`crossSectionRepository`, `writeCapableInterfaceUsedReadOnly`, `crossSectionWriteSurface`,
`duplicateDbSetOwner`, `diRegistration`.

## Conservation anchors

The Plan C gate holds a refactor to per-section anchors, diffing interface method names between
baseline and now. Anchors therefore have to track the same thing the score does: an internal
interface in the anchor set makes a later deletion of one of its methods read as
`capability-evaporation`, for surface that scores zero and no consumer can reach.

Filtered at the construction site in `SectionShapeAnalyzer` (exported interfaces, and exported
methods within them) rather than in `BuildConservationAnchors`, because `InterfaceAnchor` carries no
symbol to test. `InterfaceAnchors` has exactly one consumer, so the section-shape view is unaffected;
`SectionShape.ReadServiceInterfaces` / `FullServiceInterfaces` and the `missing*` computation keep
reading the unfiltered lists.

On Humans: 125 -> 90 anchors, 816 -> 477 anchor methods. 42% of what the gate policed was
unreachable internal surface. No score movement.

## Deliberately unchanged

- **Private types stay out of the corpus.** `ClassifyAsync` still skips
  `Accessibility.Private`, so `internalComplexityTotal` is unchanged by construction and the
  measurement below isolates one variable. Private *members* of admitted types were already scored
  by the sizing rules and still are.
- **The `missing*` rules still count internal interfaces.** `missingReadSurface` /
  `missingWriteSurface` / `missingPrimaryInfoDto` ask whether a repo-backed section has a read
  surface at all. Requiring it to be exported would fire new penalties on sections that are simply
  not extracted yet, which is a separate policy question from this one.
- **Controllers.** An MVC controller is reached by reflection, not by an assembly reference, so an
  `internal` controller's actions now score nothing. That is consistent with the definition (no
  section can call them) but leaves controller actions under-counted as an HTTP surface. Noted as
  follow-up, not resolved here.

## Measured impact (Humans, built, at v0.23.0)

| | before | after | delta |
|---|---|---|---|
| `surfaceTotal` | 55,716 | 21,010 | **-34,706 (-62.3%)** |
| `internalComplexityTotal` | 3,131 | 3,131 | 0 |
| `typesAnalyzed` | 2,816 | 2,816 | 0 |

Largest per-rule movements:

| rule | before | after | delta |
|---|---|---|---|
| `repositoryImplementationMethod` | 7,710 | 0 | -7,710 |
| `repositoryInterfaceMethod` | 7,690 | 2,060 | -5,630 |
| `controllerAction` | 5,632 | 1,600 | -4,032 |
| `applicationServiceMethod` | 4,960 | 1,775 | -3,185 |
| `methodParameterOverflow` | 3,947 | 1,211 | -2,736 |
| `fullServiceInterfaceMethod` | 5,952 | 3,272 | -2,680 |
| `dtoScalarProperty` | 4,292 | 2,060 | -2,232 |
| `publicDtoType` | 3,820 | 1,845 | -1,975 |

Both repository-implementation rules going to **exactly zero** is real, not a bug: Humans has 41
repository implementations and every one is `internal`, exposed through a public interface. The old
score charged 8,250 points for surface that does not exist.

Two structural findings fall out of the per-section movement:

- **Extracted sections go dark, as intended.** Camps -95% (3,530 -> 176), Events -97.5%,
  Surveys -95.4%, Calendar -97.9%. What survives in Camps is precisely its published contracts
  (`readServiceInterfaceMethod` 36, `publicDtoType` 20, `dtoScalarProperty` 11) plus its coupling
  (`crossSectionFullService` 48, `crossSectionReadInterface` 18). Their internal complexity is
  untouched, so a big section still reads as big.
- **The real surface is concentrated in the shared assemblies.** Six sections moved by exactly zero
  — `Application` (9,966), `Web` (4,838), `GoogleIntegration` (523), `UI` (236), `Domain` (67),
  `Interfaces` (43) — because they are all-public. `Application` alone is now 47% of the solution's
  entire surface score. That is the honest picture: Humans' published API lives in the shared
  contracts assembly, not in the sections.

## Compatibility

**Every baseline predating v0.24.0 is incomparable.** Surface totals fall by construction, so a
`--baseline` comparison against an older file reports a large fake improvement. Re-baseline
deliberately.

Output JSON shape is unchanged (verified structurally: identical top-level keys, same `groups` /
`byRule` / `conservationAnchors` layout). Only values move.

## Tests

`test/Reforge.Tests/EffectiveAccessibilityTests.cs` against fixtures in
`test/SampleSolution/SampleSolution.Reporting/EncapsulationFixtures.cs`:

- `IsExported` false for an internal type and for a `public` type nested inside one.
- Internal types remain in the corpus, so sizing rules still see them.
- Durable-surface rules skip internal declarations while an exported peer in the same section
  still scores.
- `oneImplementationInterface` skips an internal interface, still fires for an exported one.
- `crossSectionFullService` still fires on an **internal** consumer — the pin on the split.
