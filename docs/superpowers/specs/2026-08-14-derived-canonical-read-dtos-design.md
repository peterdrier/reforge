# Derived Canonical Read DTOs - Design Spec

## Status

Approved (2026-08-14). Targets reforge v0.25.0 (minor, scoring-semantics change). Fixes issue #14.

## Context

#10 deleted the section-*membership* config because assembly boundaries already state it:
structural, compiler-enforced, cannot drift. `SectionRule.CanonicalReadDtos` was the same mistake
one level down — a hand-authored list restating something the code already declares.

A canonical read DTO is a section's published read API. Under the assembly model that is not a
judgement call: it is a data type the section **exports** from its contracts surface. Two shapes
occur in the wild and both are structural:

- a `<Section>.Contracts` assembly (which already folds into its parent section, see
  `AssemblySections`), and
- a `Contracts/` folder inside the section's own assembly.

The Humans solution has 26 of the first and 15 of the second; four sections (Auth, Camps, Teams,
Tickets) have both. Neither shape is derivable from the other, so derivation unions them.

The old field also had two incompatible readings inside reforge, exposed by #12:

- `SurfaceScoreEngine.ScoreReturnTypeRules` flattened every section's list into one set of **simple
  names** and matched it solution-wide.
- `SectionShapeAnalyzer` used it **section-scoped**, as the primary/settings anchor fallback.

So the same config key meant "global name list" in one consumer and "this section's DTOs" in the
other. Deriving removes the ambiguity: membership follows the declaring assembly, exactly like
section membership, and matching is by symbol identity rather than by name.

## Goal

Derive the set. Delete the config field outright — no override, no escape hatch.

The absence is the point. A section with no contracts assembly and no contracts folder has not
declared a read API, and its score must say so. Config can no longer paper over a boundary that was
never drawn; the forcing function is to make the assembly structure correct instead of describing
it in JSON.

## Definition

`CanonicalReadDtoSet.Derive(classified)` admits a type when all of:

1. **Exported** — `SurfaceVisibility.IsExported`, i.e. `public` all the way out through every
   containing type. This is the #13 definition, unchanged.
2. **A data type** — a non-static `class` or `struct`. Interfaces, enums and delegates are not a
   read API's payload.
3. **DTO-shaped** — carries the `dto` classification tag **or** is a behavioral data carrier (at
   least one public property, no public methods). The behavioral fallback admits the DTOs whose
   names don't match the conventional suffixes (`*Hit`, `*Totals`, `*Row`) and keeps derivation
   working under a config that carries no `dto` classification rule.
4. **Declared on the contracts surface** — its declaring assembly is a `<X>.Contracts` assembly, or
   its source path has a `Contracts` directory segment.

### Location is never evidence

Point 1 is what makes point 4 safe. In Humans, `ICampContactService` and `ICampRoleService` are
`internal` declarations sitting in the `Humans.Camps.Contracts` **namespace** inside
`Humans.Camps`. A namespace or folder named `Contracts` says where the author filed a type, not
what the assembly publishes. Filtering on location alone would import internals as canonical read
DTOs. The check runs on the semantic model — `DeclaredAccessibility` walked out through containing
types — never on the name or the path alone.

This mirrors the #13 rule and the sample solution pins it: `SampleSolution.Lodge/Contracts/`
declares `LodgeStayInfo` (public, canonical) next to `LodgeSecretInfo` (internal, excluded).

Point 4 inspects **every** declaration, not just `ClassifiedType.File`. A partial type with one part
under `Contracts/` and one part outside has several source locations, and which one is "primary"
follows syntax-tree order — reading the primary alone would let a type enter and leave the set when
files are merely reordered. Declaring any part of a type on the contracts surface publishes it.

### Preference order

`ForSection` returns a section's DTOs in anchor-preference order: `*Info` names first, then
shortest name, then ordinal name, then the full type key. That is what makes `CampInfo` outrank
`CampSeasonMemberInfo` as Camp's primary anchor, and it is deterministic — the old field carried the
author's ordering, which derivation cannot recover. The type-key tiebreak is load-bearing: a section
can span two assemblies (`X` and `X.Contracts`) and many namespaces, so simple names collide, and
`List.Sort` is not stable — a comparator returning 0 would let enumeration order pick the anchor.

## Consumers

| Consumer | Before | After |
|---|---|---|
| `canonicalReadDtoReturn` credit | simple-name match against the flattened global list | symbol-identity match (`declaringAssembly\|fullyQualifiedName`) against the derived set |
| `methodReturnsEntityAcrossSection` exemption | same flattened list | same derived set |
| `SectionShapeAnalyzer.ResolvePrimary` / `ResolveSettings` | first non-`*SettingsInfo` / first `*SettingsInfo` config entry | first non-`*SettingsInfo` / first `*SettingsInfo` derived DTO of that section |

The credit stays **solution-wide**: a Tickets method returning Users's canonical DTO still earns
it. That is deliberate and consistent with #13's split — the credit charges for a *use* (what a
method hands back), not for a declaration's published shape, so it is not scoped or gated the way a
declaration rule would be. What changed is identity, not scope: two assemblies may each declare a
`UserInfo` and only one of them may be exported from a contracts surface, so the name alone can no
longer stand in for the type.

One behavioral tightening: the entity-leak exemption now holds even when
`canonicalReadDtoReturn` is weighted to `0`. Previously zeroing the credit also silently re-enabled
the penalty on canonical DTOs. A canonical DTO is the section's read API by definition; returning
one is never an entity leak, whatever the credit is worth.

## Compatibility

`SectionRule.CanonicalReadDtos` is deleted. `System.Text.Json` would drop the now-unknown key
without a word, which is exactly the silent drift this issue is about — a config still carrying the
list used to grant credit and suppress the entity penalty solution-wide, and would keep looking
like it does.

So `SectionRule` captures unrecognized members via `[JsonExtensionData]` and
`SurfaceScoreConfig.RemovedCanonicalReadDtosWarning()` names every section block that still declares
`canonicalReadDtos`. Inert *and* visible, the same treatment `unknown-config-section` gives a stale
section key. **Both** commands that resolve DTO anchors report it: `surface-score` as a
`removed-config-field` diagnostic, and `section-shape` — which loads the same config, calls the
analyzer directly, and never touches the engine — as a stderr warning. The field used to feed
`section-shape`'s primary/settings anchors, so its output moves too and must say why.

Output JSON shape is unchanged — no key added or removed at any level.

## Deliberately unchanged

- The `dto` classification rule and the `publicDtoType` / `dto*Property` weights. Derivation
  decides which DTOs are a section's *published read API*; it does not change what counts as a DTO.
- `SectionShapeAnalyzer`'s DTO-inventory descent set (every dto-tagged or data-carrier type's simple
  name). That drives recursive path expansion inside an anchor, which has nothing to do with what a
  section publishes.
- `primaryInfoDto` / `settingsInfoDto` / `cacheDto` config. Those name *which* DTO plays a role, a
  fact the assembly graph genuinely can't state; the derived set is only the fallback when the
  `<Section>Info` convention misses.

## Measured impact (Humans, built, A/B on one identical tree)

Both binaries were run back to back against one built tree and saw the same corpus —
`typesAnalyzed` 2,840, `internalComplexityTotal` 3,111, 46 sections — so the deltas are the change
and nothing else. A `Humans.Shifts` extraction is in flight in that repo, so the absolute totals
are a snapshot of a moving tree; an earlier measurement the same day read 17,033 for the same
`surfaceTotal`. Only same-tree deltas are meaningful.

| | v0.24.0 | v0.25.0 |
|---|---|---|
| `surfaceTotal` | 14,956 | 14,880 (-0.5%) |
| `canonicalReadDtoReturn` | -3 | -81 |
| `missingPrimaryInfoDto` | 240 | 110 |
| `readSurfaceProjectionMethod` | 116 | 248 |

24 of 45 scoring sections did not move at all. No other rule moved.

**The credit was granting almost nothing.** Nine config blocks listed 34 DTO names between them and
earned the credit exactly once, because the names were matched against return types solution-wide
and had drifted. Derivation credits eight sections — Application (-24), GoogleIntegration (-21),
Camps and Shifts (-12 each), Containers/Finance/Infrastructure/Tickets (-3 each) — and only two of
those had a config block. Config was describing a read API for the sections someone had thought
about, while the assemblies that actually export one went uncredited.

**Fourteen sections gained a primary anchor; one lost the one config invented for it.** Calendar
goes 0 -> 10 on `missingPrimaryInfoDto` because its configured canonical DTO, `CalendarEventInfo`,
is an `internal sealed record` under `Sections/Humans.Calendar/Services/Dtos/`. Calendar publishes
no read API; the config had been asserting one that no consumer can reach. That is the whole thesis
of the change in one section.

**Resolving anchors uncovers hidden debt.** `readSurfaceProjectionMethod` only fires for a section
with a resolved primary anchor — without one, a primitive read cannot be distinguished from a
projection. So the newly anchored sections surface projection debt that was previously invisible
(Governance +40, Expenses +32, Budget/CityPlanning/Email/GoogleIntegration +12 each). The rule
getting *more* expensive is the correct direction: those sections were escaping it by having no
read API at all.

Predictions from the issue that held: Camps and Tickets both gained credit, and the config's
`Users`/`Dashboard`/`Admin`/`Platform` blocks — which #12 had already made inert — are now moot
rather than merely ignored. Three names in that config (`ShiftInfo`, `VolunteerBuildStrip`,
`VolunteerTrackingData`) no longer exist as types at all; that is exactly the drift derivation
prevents.

## Tests

Sample-solution fixtures, both shapes plus the negatives:

- `SampleSolution.Camp.Contracts` — shape 1. `CampInfo`, `CampSettingsInfo`, `CampSummary` derive.
- `SampleSolution.Lodge/Contracts/` — shape 2. `LodgeStayInfo` derives; the adjacent `internal`
  `LodgeSecretInfo` does not. `LodgeStayInfo` deliberately does not match the `<Section>Info`
  convention, so Lodge's primary anchor can only resolve through the derived set.
- `CampStayEntity` (in `.Contracts`) vs `CampLegacyEntity` (in `SampleSolution.Camp`) — identical
  shape, both classified `entity` by name. `CampFeedReader` in Reporting returns both across the
  boundary: the exported one is credited, the other is charged `methodReturnsEntityAcrossSection`.
- Tent and Dorm have neither shape and contribute nothing; Tent's primary anchor stays unresolved
  and `missingPrimaryInfoDto` fires.
- `LodgeAmenityInfo` is `partial`, split between `Contracts/LodgeContracts.cs` and a root-level
  `AmenityPartial.cs` named so it sorts first and therefore wins the primary location. The test
  asserts that precondition explicitly, so the fixture cannot quietly stop exercising the
  multi-location path.
- `SampleSolution.Lodge.Contracts.V2.LodgeStayInfo` collides on simple name with the one in
  `.Contracts`. The tie test derives from the classified list and from its reverse and requires the
  same order out — `List.Sort` uses a stable insertion sort at these sizes, so reversing the input
  is what actually forces the untiebroken comparator to disagree with itself.

Every new test was verified to fail with the implementation reverted: dropping the `IsExported`
gate reddens the internal-exclusion test; accepting any location reddens six; removing the analyzer
fallback reddens the Lodge anchor test and the existing missing-surface test; reading only the
primary location reddens the partial test; dropping the type-key tiebreak reddens the order test.
