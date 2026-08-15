# Gate 1 fixtures

Every scored rule must come with a pair here before it ships:

- `<label>.Before.cs` — code that fires the rule.
- `<label>.CheapestFix.cs` — the laziest edit that stops it firing, as an LLM would perform it.
- `<label>.GoodFix.cs` — optional; a genuine fix, which *may* score better.

`GateOneFixtureTests` scores the sample solution once and attributes points by declaring file, then
asserts the cheapest fix **does not lower the total**. If it does, the rule is gameable: an agent
can satisfy it without improving the design, and the rule is training readers to accept a worse
codebase.

The rules a pair targets are declared in a `// gate1:` comment on the Before file. They are named
explicitly rather than inferred from what fires, because almost every fixture incidentally trips
`applicationServiceMethod`, and inferring coverage from that would mark rules as gated that nobody
designed a cheapest fix for. The test checks each named rule actually fires in the Before file, so
a stale name fails rather than silently narrowing the gate.

Fixtures must use BCL types only — the sample solution declares no `PackageReference` anywhere, and
`SampleSolutionInvariantsTests` enforces it.
