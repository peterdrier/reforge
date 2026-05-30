# Changelog

What changed and why. Newest first.

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
