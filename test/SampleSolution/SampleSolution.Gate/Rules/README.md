# Gate 1 fixtures

Every scored rule must come with a pair here before it ships:

- `<label>.Before.cs` — code that fires the rule.
- `<label>.CheapestFix.cs` — the laziest edit that stops it firing, as an LLM would perform it.
- `<label>.GoodFix.cs` — optional; a genuine fix, which *may* score better.

`GateOneFixtureTests` compiles **each variant on its own** and asserts two things about each pair:

1. **The declared rule charges strictly less in the cheapest fix.** This is what makes the fixture a
   fix. An agent edits code because a number went down; if the rule charges what it charged before,
   nobody would have made the edit and the pair demonstrates nothing. Without this, an unchanged
   copy of the Before file passes the gate and the rule is recorded as covered forever.
2. **The total does not drop.** If it does, the rule is gameable: an agent can satisfy it without
   improving the design, and the rule is training readers to accept a worse codebase.

## When a rule fails

Some rules fail. Add a `// gate1-gameable: <why the cheapest fix is degenerate>` line to the
`Before` file and the second assertion inverts: the drop becomes *required*
(`KnownGameableFixtures_StillLowerTheScore`). If someone later repairs the rule, that test fails and
says to delete the marker, which is the one moment the repair could otherwise pass unnoticed and
leave a finding standing that is no longer true.

The alternative was a red build, and a red build is not a finding — it is a blocked branch, and
blocked branches get unblocked by tuning the fixture until it passes. That is the gate becoming the
thing it exists to catch.

A rule can have more than one pair. `booleanParameter` has two: the cheapest fix is the same edit
in both (bool → two-value enum) and the verdict flips on what the enum is named, because the rule
that would catch the replacement — `mutationModeParameter` — recognises enums by type suffix and
parameter name. One pair would have reported whichever half its author wrote. When a rule's verdict
turns out to depend on something the fixture author chooses, write both.

The marker asserts something no test can check: that the cheapest fix is **degenerate** — it
satisfies the rule while leaving the design no better, ideally worse. Some rules want surface
*deleted*, and for those the honest cheapest fix is a genuine improvement whose score *should* fall;
marking that pair gameable would be a false accusation with a green check next to it. The note is
where the argument that the fix is degenerate goes, and it is required to be non-empty.

## Fixture authoring

Point 1 is why a fix that only *relocates* surface is not a cheapest fix. Splitting a six-property
DTO into a parent and a nested child drops the parent's property count, but `dtoScalarProperty`
charges per property across the variant and still charges six — the number an agent watches never
moved, so that edit belongs in a `GoodFix` discussion, not here.

## One variant, one solution

Each variant is scored as a solution containing only itself (`IsolatedVariantScorer`), so the
report's total *is* that variant's score. Nothing is filtered, nothing is attributed, and no
fixture can move another fixture's number.

This matters more than it sounds. The harness originally scored the whole sample solution once and
reconstructed each variant's total by filtering to its file, which was exact for rules that charge a
declaration and quietly wrong for every rule that charges a *section* for its shape — see
[#26](https://github.com/peterdrier/reforge/issues/26) for the three separate ways that went wrong.
Isolation removes the class of bug rather than guarding against instances of it, and it makes
section-shaped fixtures possible for the first time: a variant compiled alone **is** a section.

Two consequences for writing fixtures:

- **A fixture must compile on its own.** `EveryVariant_CompilesOnItsOwn` enforces it. A type
  borrowed from another fixture file is not there, so each file carries everything it needs. Type
  names are prefixed `Gate…` per pair to keep collisions obvious.
- **Fixtures must use BCL types only** — variants compile against the test host's reference set, and
  the sample solution declares no `PackageReference` anywhere (`SampleSolutionInvariantsTests`
  enforces the latter).

## Variants that span sections

Sections come from assembly names, so a variant compiled as one project has exactly one section and
a cross-section rule can never fire in it. That is a limit of the harness, not a property of those
rules, and left alone it would have made `crossSectionRepository` and its four siblings impossible
to fixture no matter how much fixture-writing happened.

So a variant may carry **satellite files** beside it, one per extra section:

```
crossSectionRepository.Before.cs        -> SampleSolution.Gate   (the consumer)
crossSectionRepository.Before.Camp.cs   -> SampleSolution.Camp   (what it reaches for)
```

The segment between the variant name and `.cs` is the section name; it must be a bare identifier,
which is what stops `.GoodFix.cs` from being read as a satellite of `.Before.cs`. The primary
project references every satellite and never the reverse, so which side is the *consumer* — the side
a cross-section rule charges — is unambiguous.

Inside the full sample solution these files are all just Gate; only the isolated harness splits
them. `_HarnessProbe.TwoSection*.cs` is the mechanism's own probe (`IsolatedVariantScorerTests`),
not a fixture — it is deliberately not named `*.Before.cs`, because proving the harness *can* fire a
rule is a different claim from proving that rule survives its cheapest fix.
