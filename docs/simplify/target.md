# Reforge — target design

The minimal architecture for what Reforge actually does, derived from the behavior inventory in
`as-is.md`. This is the stable map. Amend it deliberately; do not regenerate it per audit.

## What the system is

A one-shot CLI that opens a Roslyn workspace, answers one question about a C# solution, prints
structured output, and exits — plus an optional hot server that keeps the workspace warm and
answers the same questions over loopback TCP.

Two things follow from that, and they are the whole design:

1. **The set of questions is one list.** Every consumer of that list — the cold CLI, the hot
   server, the agent-facing skill doc, the README — must read it, never restate it.
2. **A question is a function of `(Solution, args) → results`.** Anything a command does before it
   has a resolved symbol is plumbing, and plumbing belongs in one place.

## Structure

```
Program.cs            relay-or-cold-start, nothing else
CommandRegistry.cs    the one list: every Command, built once, consumed by both hosts
Commands/*.cs         one file per question; body starts at "I have a resolved symbol"
SymbolCommand.cs      resolve-or-explain: the not-found / ambiguous / telemetry wrapper
Analysis/*.cs         Roslyn analysis shared across commands, no CLI or output types
  SemanticFacts.cs      one definition of "is a write", "is a repo type", "is a test path"
  SurfaceScore/*.cs     one file per scoring pass
Output/*.cs           Compact and Json emitters, limit handling
Serve/*.cs            TCP host + workspace lifecycle; routes through CommandRegistry
```

The current flat `Reforge` namespace with `Reforge.Commands` is close enough — the point is not the
folders, it is that four lists collapse to one and three write-heuristics collapse to one.

## Invariants

- **One command registry.** `Program` and `ServeCommand` build the root command from the same
  factory. Adding a command means editing one list; a command reachable cold is reachable hot.
- **A relayed command that the server cannot run is an error, not help text.** `TryRelayAsync`
  returns success only when the server actually dispatched the command. Silent success is the
  worst failure mode for an agent-facing tool, which cannot see a help screen and read it as wrong.
- **Resolve-or-explain lives once.** Not-found suggestions, ambiguity candidates, timing, and
  telemetry are wrapper concerns. A command file contains only the analysis that is unique to it.
- **One semantic judgment per concept.** "Is this a write" has exactly one implementation. Where a
  caller needs a narrower policy — `ImplementationComplexity` legitimately does — it names and
  documents the variant explicitly rather than keeping a private copy that drifts.
- **Global options are honoured or absent.** `--limit` either caps a command's output or is not
  offered on it. Accepting a flag and ignoring it is worse than rejecting it.
- **Output shape is uniform.** Every command emits through the same Compact/Json emitters, so the
  format contract is one thing to keep stable rather than 28.
- **Config carries policy, never structure.** Anything the compiler already states — section
  membership, what a section exports, what is public — is derived. This invariant is already
  established (v0.23.0, v0.25.0) and is the repo's best existing idea; keep it.
- **Every declared surface has a test that fails when it breaks.** In particular the hot path: if a
  command is in the registry, a test proves it round-trips through the server.

## Deliberately not done

- No plugin/rule-provider abstraction for audit commands. There is one consumer; a static list is
  correct until there are two.
- No `IScoreRule` interface for the ~45 scoring rules. Splitting `SurfaceScoreEngine` by pass is a
  file split, not an abstraction — the rules share too much per-pass state to be worth reifying.
- No persistent daemon, LSP, or IDE protocol. Stated non-goal in `CLAUDE.md`; the hot server is a
  cache, not a service.
- No consolidation of the 28 commands into fewer parameterized ones. Reviewed 2026-08-14 and
  declined; the agent-facing value is that each question has a name.
