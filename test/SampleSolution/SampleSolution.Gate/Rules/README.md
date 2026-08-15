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

Fixtures must use BCL types only — the sample solution declares no `PackageReference` anywhere, and
`SampleSolutionInvariantsTests` enforces it.
