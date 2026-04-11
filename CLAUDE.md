# Reforge

Roslyn-powered semantic query and refactoring CLI for AI coding assistants.

## Vision

AI coding assistants (Claude Code, Copilot, Codex, etc.) refactor C# code by doing text surgery — grepping for symbols, reading files to infer types, pattern-matching strings. 80% of the work is reconnaissance. The edits are the easy part.

Roslyn already has the answer to every reconnaissance question: the full semantic model with every symbol, reference, type relationship, inheritance chain, and scope. But no tool exposes this to AI assistants in a CLI-friendly way.

**Reforge bridges the gap.** The AI decides *what* to change. Reforge provides *complete, accurate information* about the code structure needed to make the change, and executes mechanical transforms (rename, inject, move) with full semantic correctness across the entire solution.

## Primary Mission

**Collapse the reconnaissance phase of AI-assisted refactoring from many rounds of grep/read/infer to single precise queries.**

The measure of success: a refactoring task that currently takes 10 rounds of grep/read/edit/build should take 2 — one query round, one edit round.

## Goals (Priority Order)

1. **Collapse reconnaissance to single queries.** "Who depends on this?" "Where is this injected?" "What does this controller touch?" — answered completely and instantly from the semantic model.

2. **Guarantee completeness.** Every reference to a symbol — including behind interfaces, in LINQ expressions, in `nameof()`, in attribute arguments. Grep misses these. Missed references are where refactoring bugs come from.

3. **Execute mechanical transforms correctly.** Rename, inject a dependency, move a method — deterministic operations that Roslyn can do perfectly across the whole solution in one shot.

4. **AI-native output.** Structured, concise, actionable. Optimized for LLM context windows, not human IDE UIs. File paths, line numbers, and just enough context to act on.

## Non-Goals

- Not replacing Rider/ReSharper for humans
- Not an analyzer framework (that exists — Roslyn analyzers, Roslynator)
- Not automating judgment calls — the AI decides what to change, Reforge provides the information and executes the mechanics
- Not a language server (no persistent process, no IDE protocol) — it's a CLI that opens a workspace, answers a question, and exits

## Architecture

### Core Stack

- **.NET 10** console application
- **Microsoft.CodeAnalysis.Workspaces.MSBuild** — opens solutions/projects via MSBuildWorkspace
- **Microsoft.Build.Locator** — finds the installed .NET SDK for MSBuild resolution
- **Microsoft.CodeAnalysis.CSharp.Workspaces** — C# semantic model, syntax trees, symbol APIs
- **System.CommandLine** — CLI parsing (v3 preview)
- **Packaged as a dotnet tool** (`PackAsTool=true`, command name: `reforge`)

### How It Works

1. User runs `reforge <command> --solution <path> [args]`
2. MSBuildWorkspace opens the solution and compiles the semantic model
3. The command queries or transforms the semantic model
4. Results are written to stdout in the requested format

### Output Format

**Default: JSON** — machine-parseable for AI assistant consumption.

```json
{
  "command": "references",
  "symbol": "HumansDbContext",
  "results": [
    {
      "file": "src/Controllers/ProfileController.cs",
      "line": 42,
      "column": 12,
      "context": "private readonly HumansDbContext _context;",
      "containingSymbol": "ProfileController"
    }
  ],
  "total": 1
}
```

**Optional: `--format table`** — human-readable for interactive use.

```
src/Controllers/ProfileController.cs:42  ProfileController  private readonly HumansDbContext _context;
```

**Design principles for output:**
- Always include file path and line number (AI assistants need these to navigate)
- Include a context snippet (the line of code) so the AI doesn't need a separate Read call
- Include the containing symbol name for scoping context
- Keep it concise — no decoration, no color codes, no progress bars
- Total count at the end so the AI knows if the result set is complete

### Solution Path Resolution

Commands accept `--solution <path>` to specify the target. If omitted, search upward from the current directory for `*.slnx` or `*.sln` files (prefer `.slnx`). If multiple found, error with a list of candidates.

## Phases

### Phase 1 — Semantic Queries (build first, highest leverage)

These replace multi-round grep/read cycles with single precise queries:

| Command | Purpose | Key Roslyn API |
|---------|---------|----------------|
| `reforge references <symbol>` | All references to a symbol, solution-wide | `SymbolFinder.FindReferencesAsync` |
| `reforge implementations <interface>` | All types implementing an interface | `SymbolFinder.FindImplementationsAsync` |
| `reforge callers <method>` | All callers of a method | `SymbolFinder.FindCallersAsync` |
| `reforge call-chain <method>` | Transitive callers (who calls who calls this) | Recursive `FindCallersAsync` |
| `reforge usages <type> [--in <namespace>]` | Where a type is used (fields, locals, params, return types) | `SymbolFinder.FindReferencesAsync` + filter |
| `reforge injected <type>` | Classes that inject this type via constructor | Constructor parameter analysis |
| `reforge inheritors <type>` | All types deriving from a base type | `SymbolFinder.FindDerivedClassesAsync` |
| `reforge members <type>` | List members with types, visibility, modifiers | `INamedTypeSymbol.GetMembers()` |
| `reforge dependencies <class>` | What types a class depends on (constructor, fields, method params) | Symbol analysis of constructor + fields |
| `reforge parameters [--name <pattern>] [--type <type>]` | Find method parameters matching criteria | Iterate all method symbols, filter params |

**Symbol resolution:** The `<symbol>` argument is a string that Reforge resolves against the semantic model. Support these formats:
- Simple name: `HumansDbContext` (search all symbols, error if ambiguous)
- Qualified name: `Humans.Infrastructure.Data.HumansDbContext`
- Member: `ProfileService.GetProfileAsync`

If a simple name is ambiguous (multiple matches), return all candidates with their fully qualified names so the caller can disambiguate.

### Phase 2 — Mechanical Transforms

Deterministic, semantic-aware code modifications:

| Command | Purpose | Key Roslyn API |
|---------|---------|----------------|
| `reforge rename <symbol> <newname>` | Rename with full semantic awareness | `Renamer.RenameSymbolAsync` |
| `reforge inject <type> --into <class>` | Add constructor parameter + private readonly field | `DocumentEditor` |
| `reforge move-method <class.method> --to <class>` | Move a method, update all references | `DocumentEditor` + reference rewriting |
| `reforge remove-parameter <method> <param>` | Remove a parameter, update all callsites | `DocumentEditor` + `SymbolFinder` |
| `reforge extract-interface <class> [--members m1,m2]` | Extract interface from class | `DocumentEditor` + new file creation |

**Transform safety:**
- All transforms produce a diff preview first (unless `--apply` is passed)
- The diff shows every file that will be changed and the exact modifications
- After applying, verify the solution still compiles (`Workspace.TryApplyChanges` + diagnostic check)
- If compilation fails after transform, revert and report the errors

### Phase 3 — Design Rule Auditing

Pluggable rules for verifying architectural constraints:

| Command | Purpose |
|---------|---------|
| `reforge audit --rule <name> --solution <path>` | Run a specific rule |
| `reforge audit --all --solution <path>` | Run all rules |

Rules are classes implementing a simple interface. Examples:
- `no-dbcontext-in-controllers` — controllers must not reference DbContext
- `no-privileged-booleans` — service methods must not take `isPrivileged`/`isAdmin` bool params
- `services-via-interfaces` — classes should depend on service interfaces, not concrete types
- `single-table-owner` — a DbSet should only be accessed by its owning service

These rules are specific to the consuming project, not built into Reforge. They could live in a separate assembly or be configured via a rules file. Design the rule interface first, defer the plugin loading mechanism.

## Build Commands

```bash
dotnet build Reforge.slnx
dotnet run --project src/Reforge -- <command> [args]
```

To install as a global tool for testing:
```bash
dotnet pack src/Reforge
dotnet tool install --global --add-source src/Reforge/nupkg Reforge
```

## Development Guidelines

- **Test with a real solution.** The Humans project at `H:\source\Humans\Humans.slnx` is the primary test target. Always verify commands work against it.
- **MSBuildWorkspace quirks.** Opening a workspace can produce diagnostic warnings (missing SDKs, target framework issues). Log these to stderr, not stdout. Don't fail on warnings — many are benign.
- **Startup time matters.** Opening an MSBuildWorkspace and compiling the semantic model takes seconds. For a CLI tool this is acceptable (it's a one-shot command, not interactive). But avoid unnecessary recompilation — open the workspace once, run the query, exit.
- **Error messages should be actionable.** "Symbol not found" is useless. "Symbol 'Foo' not found. Did you mean: Humans.Core.Foo, Humans.Web.Foo?" is useful.
- **Keep it simple.** This is a CLI tool, not a framework. Don't over-abstract. A command is a function that takes args, opens a workspace, queries the model, and prints results. If a command is under 100 lines, it's fine as a single file.
