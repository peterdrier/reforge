# Reforge

Roslyn-powered semantic query CLI for AI coding assistants.

## What It Does

AI coding assistants refactor C# by doing text surgery — grepping for symbols, reading files to infer types, pattern-matching strings. 80% of the work is reconnaissance. Reforge collapses that to single precise queries using the Roslyn semantic model.

```
$ reforge injected IUserService --solution MyApp.slnx

3 injected of MyApp.Core.IUserService

MyApp.Services/CachedUserService.cs
  19: CachedUserService(IUserService inner)

MyApp.Services/OrderService.cs
  14: OrderService(IUserService userService)

MyApp.Web/Controllers/UserController.cs
  16: UserController(IUserService userService)
```

Every reference is found via the compiler's semantic model — including references that grep misses (interface dispatch, `nameof()`, attributes, LINQ expressions, generic type arguments).

## Commands

| Command | Purpose |
|---------|---------|
| `reforge references <symbol>` | All references to a symbol, solution-wide |
| `reforge callers <method>` | Direct callers of a method |
| `reforge call-chain <method>` | Transitive callers with depth tracking |
| `reforge implementations <interface>` | Types implementing an interface |
| `reforge inheritors <type>` | Types deriving from a base class |
| `reforge members <type>` | All members with signatures and visibility |
| `reforge dependencies <class>` | What a class depends on (ctor, fields, props) |
| `reforge injected <type>` | Who injects this type via constructor |
| `reforge usages <type>` | Where a type is used, categorized by kind |
| `reforge parameters --name X --type Y` | Find parameters matching patterns |
| `reforge audit-surface <type>` | Per-method inbound view: caller counts (prod/test) + body shape for classes (passthrough-repo/-service/-self, linq-over-repo/-service, write, composite, complex) |
| `reforge audit-downstream <class>` | Per-method outbound view: dependency calls, DbSet reads/writes traced one hop through repository implementations with `via` attribution, untraced repo-to-repo hops, and external IO |

## Surface Score

`reforge surface-score` scores a solution's durable public surface (and, on a separate axis, the
implementation complexity hiding behind it), grouped by **section**.

**A section is an assembly.** There is nothing to configure: every non-test project is a section,
`<X>.Contracts` folds into `<X>`, and the dot-segment prefix shared by all of them is stripped for
display — `Humans.Store` + `Humans.Store.Contracts` both report as `Store`. Assembly membership is
structural and compiler-enforced, so it can't drift from the solution the way a path/namespace/symbol
glob does. A monolith that hasn't been split yet simply scores under its own assembly name until it is.

**Surface is what the assembly exports.** Only effectively-public declarations score on the surface
axis — `public` all the way out through every containing type. A `public` method on an `internal`
class, or a `public` type nested in an `internal` one, is implementation: no other section can call
it, so nothing outside can break when it changes. (`protected` doesn't count as exported, and
`InternalsVisibleTo` doesn't widen surface.) Internal and private code still scores in full on the
**internal-complexity axis**, so a well-encapsulated section reads as small surface + whatever
complexity it actually carries.

Rules that charge for a *use* rather than a declaration — `crossSectionRepository`,
`crossSectionFullService`, `crossSectionReadInterface`, `writeCapableInterfaceUsedReadOnly`,
`duplicateDbSetOwner`, `diRegistration` — are **not** gated this way:
an internal class injecting another section's repository still forces the assembly reference and
still calls across the boundary, so marking it internal can't make the coupling free.

**A section's read API is what it exports from its contracts surface.** Canonical read DTOs are
derived, never listed: the exported data types a section declares in its `<Section>.Contracts`
assembly, or under a `Contracts/` folder in its own assembly. A `Contracts` folder or namespace is
a location, not evidence — an `internal` type declared there is still not surface. A section with
neither shape has not declared a read API, and the score says so rather than letting config paper
over a boundary that was never drawn.

`reforge.surface-score.json` (searched upward from the solution) is optional and carries **policy
only**:

```jsonc
{
  "classifications": { /* name/path/attribute patterns -> role tags */ },
  "weights":         { /* per-rule points; 0 disables a rule */ }
}
```

There is no `sections` block. Sections are the solution's assemblies, a section's canonical read
DTOs come from what it exports, and its surface expectations from whether it declares a repository
or a DbContext — so nothing about a section is config's to state. A file still carrying the key
loads, and every key this version does not read is named as `removed-config-field`.

**A degraded build is refused, not scored.** If the solution doesn't compile cleanly, both
`surface-score` and `section-shape` print nothing and exit **2** (distinct from 1, so a broken tree
is machine-distinguishable from a broken tool). stderr carries the error and unresolved-reference
counts plus the individual errors with file and line. A partial score reads as authoritative and
has been quoted from broken trees before — two runs against the same broken tree agree with each
other and are wrong together, so matching totals are not evidence of soundness. Pass
`--allow-degraded` to analyze anyway; it prints the result, marks it degraded in every format, and
exits 0.

**Every section reports its size beside its score.** A score number alone can't tell you whether
a section's points fell because its API shrank or because its code did — and most
internal-complexity points are satisfiable by edits that improve nothing. So each group carries a
`metrics` block, with a solution-level rollup beside `typesAnalyzed`:

```json
"metrics": {
  "locProd": 767, "files": 15, "classes": 25, "interfaces": 3, "methods": 67,
  "cognitive":  { "avg": 0.82, "p95": 2, "max": 35, "maxMethod": "ReportBuilder.BuildEverything" },
  "cyclomatic": { "avg": 1.72, "p95": 5, "max": 17, "maxMethod": "ReportBuilder.BuildEverything" },
  "maxClassLoc": 150, "maxClassLocName": "UserService"
}
```

Compact and markdown print the same numbers inline — LOC plus a cognitive figure per section.
Cognitive complexity is the metric the internal axis actually scores; cyclomatic is carried for
continuity with the `snapshot` history series, which has always recorded it solution-wide.

The corpus is the **scoring** corpus, so a metric and a score always describe the same code: no
test projects (they are measured separately — see below), no generated code (EF
migrations, `*.g.cs`, `*.Designer.cs` — excluded from the internal axis too), and complexity
measured only over methods that have a body. `maxClassLoc` covers classes and structs — the same
set `classes` counts, which is deliberately wider than the set the `largeClass` rule scores (that
rule tracks only application services, repository implementations, controllers and background jobs).
The block describes the section's size, so it reports the section's largest class even when no rule
currently charges for it. With `--group` set, the top-level rollup
scopes to that section, the way `byRule` already does. `--list-groups` carries `locProd` for every
section — including sections that scored nothing — so sections can be ranked by size without
pulling a full report.

Metrics are informational: nothing here feeds the score, so totals are unaffected by their presence.

**Test mass is reported per section, and scored nowhere.** Test projects are outside the scoring
corpus by construction — everything in one is public, so every surface rule would fire on code no
other section can call — which also made a section's test size invisible. Each group now carries a
`tests` block beside its `metrics`:

```json
"tests": { "loc": 3410, "files": 22, "projects": 2, "locVsProdPercent": 77 }
```

`locVsProdPercent` is the figure that compares two sections: raw test LOC scales with section size,
so the biggest section carries the most tests by default and the ratio is what distinguishes them.

A test project belongs to the section its **non-test project references** name, never to its own
name — `X.Tests` and `X.IntegrationTests` have to land in the same column, and a suite named after
nothing still tests something. Where the references name several sections (an integration suite
reaching past the section under test) the project name breaks the tie; where nothing breaks it, the
project is listed in `unattributedTestProjects` with an `info` diagnostic, and its mass is in the
solution rollup and in no column. Nothing about the test corpus is scored: there is no test axis and
no weight reads any of these numbers.

`reforge section-shape` renders the same sections as a report (interfaces, DTO anchors,
cross-section use, missing surfaces, visible debt, advisories).

## Install

Requires [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```bash
dotnet tool install --global Reforge
```

To upgrade an existing install:

```bash
dotnet tool update --global Reforge
```

Works the same on Linux, macOS, and Windows (PowerShell or cmd). Make sure `~/.dotnet/tools` (Linux/macOS) or `%USERPROFILE%\.dotnet\tools` (Windows) is on your `PATH`.

### Build from source

```bash
git clone https://github.com/peterdrier/reforge.git
cd reforge
dotnet pack src/Reforge -o src/Reforge/nupkg
dotnet tool install --global --add-source src/Reforge/nupkg Reforge
```

### Claude Code integration

```bash
reforge install
```

This registers reforge as a Claude Code skill globally. Claude will discover it automatically and run `reforge skill` to learn how to use it.

## Hot Mode

First query pays a cold start tax (3-20s depending on solution size). For repeated queries, start a hot server:

```bash
reforge serve --solution path/to/Solution.slnx &
```

Subsequent commands auto-detect the server and relay queries — ~200ms instead of seconds.

Stop it with `reforge stop` (cleans up the `.reforge-port` file, and removes a stale one if the server was hard-killed). The server also auto-shuts-down after 5 minutes of query inactivity; tune with `--idle-timeout <minutes>` (0 disables).

## Options

| Option | Description |
|--------|-------------|
| `--solution <path>` | Solution file. If omitted, searches upward for `.slnx`/`.sln` |
| `--format <Compact\|Json>` | Output format (default: Compact) |
| `--limit <n>` | Cap results. Shows "10 of 325" so you know it's truncated |

## Symbol Resolution

Symbols can be specified as:
- **Simple name:** `UserService` — errors if ambiguous, suggests candidates
- **Qualified name:** `MyApp.Services.UserService` — partial or full namespace
- **Member access:** `UserService.GetUserAsync` — type then member

## Self-Improving

Reforge is built by an AI assistant, for AI assistants. It includes a feedback loop:

- **Telemetry:** Every command logs to `~/.reforge/usage.log` (command, args, result count, timing)
- **Requests:** `reforge request "description"` logs what's missing to `~/.reforge/requests.log`

These logs feed into future development sessions to prioritize what to build next.

## Development

```bash
dotnet build Reforge.slnx          # build everything
dotnet test                         # 22 tests against SampleSolution
dotnet run --project src/Reforge -- references UserService --solution test/SampleSolution/SampleSolution.slnx
```

## License

[GNU Affero General Public License v3.0](LICENSE)
