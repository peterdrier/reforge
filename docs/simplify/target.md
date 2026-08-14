# Reforge — target design

The minimal architecture for what Reforge does, derived from the **behavior** inventory in
`as-is.md` — deliberately not from the current code's defects. This is the stable map. Amend it
when the behavior changes; do not regenerate it per audit.

## What the system does

An AI agent has a C# solution and a question about it. Reforge opens a Roslyn workspace, answers
one question, prints structured output, and exits. A hot server keeps the workspace warm and
answers the same questions over loopback TCP. Per `CLAUDE.md`, the system also intends to
**execute mechanical edits** — that half is unbuilt (see "The transform seam").

## The questions are four shapes, not twenty-eight

Sorting the commands by what they *do* rather than what they're named:

| Shape | Behavior | Commands |
|---|---|---|
| **A — symbol relation** | resolve a symbol → walk one Roslyn relation → emit locations | `references` `callers` `call-chain` `usages` `injected` `implementations` `inheritors` `members` `dependencies` `parameters` |
| **B — member classification** | resolve a type → classify each member's inbound/outbound edges → emit a per-member table | `audit-surface` `audit-downstream` `dbset-usage` |
| **C — sweep + predicate** | sweep documents → apply a rule predicate → emit offenders | `audit-auth` `audit-cache` `audit-immutable` `audit-ef` `ownership-violations` |
| **D — solution aggregate** | sweep → accumulate → emit a structured report | `surface-score` `section-shape` `snapshot` `health` `cycles` `service-map` |

Plus plumbing that answers no question: `serve` `stop` `skill` `install` `request`.

**Twenty-eight names over four shapes is the design.** The names are the product — an agent
benefits from each question having one — and they were deliberately kept on 2026-08-14. The
accidental part is twenty-eight *implementations*: a command should be a name, a shape, and the
one thing unique to it.

This is the frame the rest of the page follows from, and it is what the first pass of this audit
missed. Reading the code bottom-up finds duplication; reading the behavior top-down finds that the
duplication has a shape, and that one abstraction (`OutputFormatter`) covers shape A only while B,
C and D improvise around it.

## Structure

```
Program.cs            relay-or-cold-start, nothing else
CommandRegistry.cs    the one list of questions; consumed by both hosts and by the docs
Commands/*.cs         one file per question: a name, a shape, and what's unique to it
Runners/
  SymbolRelation.cs     shape A: resolve-or-explain, walk, emit
  MemberTable.cs        shape B
  DocumentSweep.cs      shape C: takes a rule predicate
  SolutionReport.cs     shape D
Analysis/
  SemanticFacts.cs      one definition of "is a write", "is a repo type", "is a test path"
  SurfaceScore/*.cs     one file per scoring pass
Output/
  Locations.cs          shape A emitter
  Table.cs              shapes B, C
  Report.cs             shape D
Serve/*.cs            TCP host + workspace lifecycle; routes through CommandRegistry
```

Folder names matter less than the two collapses: four command-list restatements become one, and
one output abstraction plus eight escape hatches become four emitters that match four shapes.

## Invariants

- **One command registry.** `Program`, `ServeCommand`, the skill doc and the README all read one
  list. Adding a command means editing one place; a command reachable cold is reachable hot.
- **A relayed command the server cannot run is an error, not help text.** `TryRelayAsync` reports
  success only when the server actually dispatched. Silent success is the worst failure mode for
  an agent-facing tool, which cannot see a help screen and infer something went wrong.
- **Resolve-or-explain lives once.** Not-found suggestions, ambiguity candidates, timing and
  telemetry are wrapper concerns, shared by shapes A and B — and by transforms when they arrive.
- **Every shape has an emitter; no command hand-rolls output.** The Json contract is what agents
  parse, so it is one thing to keep stable, not twenty-eight. An escape hatch from the output layer
  is evidence the shape list is wrong, not evidence the command is special.
- **One semantic judgment per concept.** "Is this a write" has exactly one implementation. A caller
  needing a narrower policy — `ImplementationComplexity` legitimately does — names and documents
  the variant rather than keeping a private copy that drifts.
- **Rules are data, not commands.** A shape-C rule is a predicate plus a message. Four rules means
  four entries, not four `foreach project / foreach document` sweeps. This is what `CLAUDE.md`
  Phase 3 already specifies.
- **Global options are honoured or absent.** `--limit` either caps a command's output or is not
  offered on it. Accepting a flag and ignoring it is worse than rejecting it.
- **Config carries policy, never structure.** Anything the compiler already states — section
  membership, what a section exports, what is public — is derived. Established by v0.23.0 and
  v0.25.0; the repo's best existing idea. Keep it.
- **Every declared surface has a test that fails when it breaks** — in particular the hot path: if
  a command is in the registry, a test proves it round-trips through the server.

## The transform seam

`CLAUDE.md` describes three phases. Phase 2 — `rename`, `inject`, `move-method`,
`remove-parameter`, `extract-interface` — is unbuilt: no `Renamer`, no `DocumentEditor`, no
`TryApplyChanges` anywhere in the tree, just a `// future` comment at `Program.cs:88`.

Building it is **out of scope for simplification** and is not in the backlog. But the ideal has to
reserve its seam, because two backlog items are shaped by whether it exists:

- A transform is `resolve → edit → diff → verify-compiles → apply-or-revert`. Its first step is the
  same resolve-or-explain that shapes A and B need, so that wrapper should be extractable by a
  caller that returns a diff rather than a result list.
- Transforms mutate the workspace. The hot server currently assumes a read-only `Solution` it can
  swap freely and processes clients sequentially for that reason. A transform arriving later either
  runs cold-only or forces that lifecycle open — worth deciding before, not after.

## Deliberately not done

- **No consolidation of the 28 command names.** Reviewed 2026-08-14 and declined. The names are the
  agent-facing product; only the implementations collapse.
- **No `IScoreRule` interface for the ~45 scoring rules.** Splitting `SurfaceScoreEngine` by pass is
  a file split, not an abstraction — the rules share too much per-pass state to reify. Note this is
  the opposite call from shape-C rules, which genuinely are independent predicates.
- **No plugin loading for audit rules.** A static list of rule objects is right until rules need to
  live outside this assembly. `CLAUDE.md` says the same: "design the rule interface first, defer the
  plugin loading mechanism."
- **No persistent daemon, LSP, or IDE protocol.** Stated non-goal; the hot server is a cache, not a
  service.
