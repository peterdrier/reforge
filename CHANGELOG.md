# Changelog

What changed and why. Newest first.

## v0.19.0 - section architecture

surface-score now understands section architecture: it resolves each configured section's
shape (owned repositories, read/full service interfaces, primary/settings/cache DTOs,
documented read shards, charged read methods, cross-section use) and scores five new
surface-axis rules from it. A new `section-shape` command renders the full shape for an
agent. Why: the prior rules saw individual symbols, not the read/write/DTO contract a
section is supposed to keep; these rules make a leaking or incomplete section boundary
visible, and the conservation anchors give Plan C's gate a stable identity to hold a
refactor to.

- New shared `SectionShapeAnalyzer` consumes the Plan A `SolutionClassifier` output and
  resolves, per configured section: read/full interface pairing, primary/settings DTO
  anchors (explicit config, the `<Section>Info` / `<Section>SettingsInfo` convention, or a
  `canonicalReadDtos` fallback so a plural section name like `Camps` still resolves the
  singular `CampInfo`) with recursive member-path inventories, cache DTO (configured,
  inferred from a `Cached*`/`*Cache`
  decorator's cache-field value type, or default-to-primary), charged read methods (via
  the behavioral `ReadSurface` classifier), missing surfaces (gated to repo-backed via
  `SectionFacts`), and cross-section write-surface use with escape analysis.
- Five new surface-axis rules (weights): `crossSectionWriteSurface` (15) when a class in
  one section injects another section's write/full interface but every observed call is
  read-covered and the dependency does not escape analysis; `missingReadSurface` /
  `missingWriteSurface` / `missingPrimaryInfoDto` (10) for a repo-backed section lacking
  that surface; `readSurfaceProjectionMethod` (4) per read-interface method that returns a
  projection/predicate/scalar/composed view instead of the section's primary Info DTO.
- Escape analysis: when an injected cross-section write dependency is passed onward,
  returned, captured, or otherwise escapes, the read-only verdict is unconfirmed, so a
  `crossSectionWriteSurfaceUnverified` advisory diagnostic is emitted instead of a
  confident penalty. The confident cross-section penalty suppresses the generic
  `writeCapableInterfaceUsedReadOnly` for that pair. Grandfathered dependencies
  (`grandfatheredDependencies`) and escape-hatch reads (`escapeHatchReadMethods`) are
  exempt from the section penalties as documented visible debt.
- New additive `conservationAnchors` key in `--format json`: per section, each canonical
  DTO (FQ-keyed, with recursive member paths) and each read/full interface (with method
  signatures + attributed surface points). Always emitted, independent of any top cap.
- New `reforge section-shape` command (Compact / Markdown / JSON): renders each section's
  resolved shape plus advisory candidates (derivable reads, missing info facts,
  cache-answerable facts, cross-section unverified).
- Behavioral, not nominal: read-method charging and cross-section read-cover are decided
  by return/call shape and escape analysis, never by name globs. All five rules are
  penalties or surcharges; advisories are zero-point.

## v0.18.1 - degraded-build detection (issue #9)

surface-score now detects when it analyzed a solution that did not compile cleanly
and would otherwise silently under-count cross-project rules (diRegistration,
crossSectionFullService, crossSectionReadInterface, methodReturnsEntityAcrossSection).
Why: a partial score read as complete corrupts baseline comparisons - and Plan C's
conservation gate compares two scores per commit, so this had to land first.

- New `BuildInspector` counts error-severity diagnostics + the unresolved-reference
  subset (CS0246/CS0234/CS0012) across all project compilations, plus a best-effort
  "appears unbuilt" filesystem probe (no `*.cs` under any project `obj/`).
- New additive `build` object in `--format json`:
  `{ degraded, compilationErrorCount, unresolvedReferenceCount, appearsUnbuilt }`.
  The existing `diagnostics` array is unchanged.
- A `degraded-build` warning is pushed into the diagnostics array (compact + markdown
  render it) and a prominent `WARNING:` line is written to stderr (never stdout).
- Diagnostic-only: no score math changed. Exit code stays 0; gating is deferred to Plan C.
