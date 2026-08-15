# Gate 1 fixtures

Every scored rule must come with a pair here before it ships:

- `<label>.Before.cs` — code that fires the rule.
- `<label>.CheapestFix.cs` — the laziest edit that stops it firing, as an LLM would perform it.
- `<label>.GoodFix.cs` — optional; a genuine fix, which *may* score better.

`GateOneFixtureTests` scores the sample solution once and attributes points by declaring file, then
asserts two things about each pair:

1. **The declared rule charges strictly less in the cheapest fix.** This is what makes the fixture a
   fix. An agent edits code because a number went down; if the rule charges what it charged before,
   nobody would have made the edit and the pair demonstrates nothing. Without this, an unchanged
   copy of the Before file passes the gate and the rule is recorded as covered forever.
2. **The total does not drop.** If it does, the rule is gameable: an agent can satisfy it without
   improving the design, and the rule is training readers to accept a worse codebase.

Point 1 is why a fix that only *relocates* surface is not a cheapest fix. Splitting a six-property
DTO into a parent and a nested child drops the parent's property count, but `dtoScalarProperty`
charges per property across the file and still charges six — the number an agent watches never
moved, so that edit belongs in a `GoodFix` discussion, not here.

The rules a pair targets are declared in a `// gate1:` comment on the Before file. They are named
explicitly rather than inferred from what fires, because almost every fixture incidentally trips
`applicationServiceMethod`, and inferring coverage from that would mark rules as gated that nobody
designed a cheapest fix for. The test checks each named rule actually fires in the Before file, so
a stale name fails rather than silently narrowing the gate.

## What this harness can and cannot measure

Every variant is scored *in the same compilation* — the solution is scored once and each variant's
total is reconstructed by filtering the report to its file. That is exact for **per-declaration**
rules and wrong for anything else, so:

- **Fixtures must be self-contained.** No type declared in one fixture file may be referenced by
  another. Rules like `oneImplementationInterface` and `duplicateDbSetOwner` depend on what the
  *rest* of the solution declares, so two fixtures sharing an interface would silently move each
  other's score. Type names are prefixed `Gate…` per pair to keep this obvious.
- **Section-level rules cannot be gated here.** `ScoreSectionArchitecture` records them against the
  section with an empty file, which the filter discards, and they are shared by every pair in the
  section rather than belonging to one. `NoGateFixture_ScoresBelowTheFileComparison` fails if a
  fixture puts the section into such a state — most likely by declaring a repository, which makes
  the section repo-backed and turns on the `missing*` rules — so the gate breaks loudly instead of
  measuring the wrong number. Gating those rules needs each variant scored in its own solution;
  that harness is the same one the `missing*` exemptions in `GateOneFixtureTests` are waiting on.

Fixtures must use BCL types only — the sample solution declares no `PackageReference` anywhere, and
`SampleSolutionInvariantsTests` enforces it.
