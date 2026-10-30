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
| `reforge snapshot [--by-assembly] [--coverage report.xml] [--append file.csv]` | Macro health record for time-series charting; `--by-assembly` emits one row per section with size, complexity, surface census, test mass and coverage |
| `reforge audit-surface <type>` | Per-method inbound view: caller counts (prod/test) + body shape for classes (passthrough-repo/-service/-self, linq-over-repo/-service, write, composite, complex) |
| `reforge audit-downstream <class>` | Per-method outbound view: dependency calls, DbSet reads/writes traced one hop through repository implementations with `via` attribution, untraced repo-to-repo hops, and external IO |

## Surface Score

`reforge surface-score` scores a solution's durable public surface (and, on a separate axis, the
shape of the implementation hiding behind it), grouped by **section**.

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
**implementation-shape axis**, so a well-encapsulated section reads as small surface + whatever
shape its implementation actually has.

The axis is named for what it measures, not for an accessibility level. `implementationShape` has no
`internal` gate: it is a fixed set of rules (`methodComplexity`, `methodCount`, `largeClass`,
`actionDispatcher`, `mutationModeParameter`, `flagsControlFlow`, and with token counts `sectionSize`,
`classSize`, `testSize`), and public code scores on it too.
Its rules charge for shapes an edit can genuinely improve: a class carrying too much, a parameter
that switches a method between two behaviours.

**Contents are a census.** Every declaration that carries code (method, constructor, computed
property, delegate-valued field, local function) pays `methodCount` (3) flat, and its own
cognitive complexity `cc` as `methodComplexity`, whatever its accessibility: `cc` itself up to
`complexityThreshold` (10), plus `complexityTaxPercent` (16%) of the excess raised to
`complexityTaxExponent` (1.5). Below the threshold logic costs the same wherever it sits and only the
flat charge moves, so tiny pieces get merged; above it the tax outgrows the flat charge, so huge
methods get split, a two-way split paying from complexity 18. Fifty points of complexity score 93 as
one method, 65 as five methods at 10 and 125 as 25 one-liners; a method at 156 costs 441. All five
numbers are proposals: `surface-score --format json` carries a `contents` block (declarations by
accessibility, complexity bands, callers, reach and depth) to tune them against. A declaration with
two or more callers skips the flat charge: merging reused code away means a copy per caller.
`complexityDepthPercent` and `complexitySharingPercent` scale a declaration's complexity by its call
depth from an entry point and by how many members reach it; both are off by default, because on a
layered codebase they mostly measure the layering.

**Exported methods are priced by what they do, not by what their type is called.** Every public
method on an exported class or interface (outside the DTO, repository, controller and background-job
rules, which keep their own pricing) is charged `exportedWriteMethod` (12) when it writes and
`exportedReadMethod` (4) when it reads. A write is a method whose body, or any corpus method it
calls, commits persistent state, or that is command-shaped (returns no data); an interface method
takes its implementations' verdict. The verdict follows calls so that returning a `bool` from
`UpdateAsync` cannot turn it into a read while it still reaches `SaveChanges` through a repository.
`exportedUnusedExternally` (2) is added for a method no other production assembly names in source:
its cheapest fix, making it internal, also clears the method's own charge, which is the point.

Rules that charge for a *use* rather than a declaration — `crossSectionRepository`,
`crossSectionFullService`, `crossSectionReadInterface`, `writeCapableInterfaceUsedReadOnly`,
`duplicateDbSetOwner` — are **not** gated this way:
an internal class injecting another section's repository still forces the assembly reference and
still calls across the boundary, so marking it internal can't make the coupling free.

**A section's read API is what it exports from its contracts surface.** Canonical read DTOs are
derived, never listed: the exported data types a section declares in its `<Section>.Contracts`
assembly, or under a `Contracts/` folder in its own assembly. A `Contracts` folder or namespace is
a location, not evidence — an `internal` type declared there is still not surface. They anchor the
`section-shape` report and the baseline conservation gate; no rule charges a section for lacking
one, for lacking a read or write interface, or for what its read methods return. The score prices
what an assembly exports, not what a layering convention says it should export.

`reforge.surface-score.json` (searched upward from the solution) is optional and carries **policy
only**:

```jsonc
{
  "classifications": { /* name/path/attribute patterns -> role tags */ },
  "weights":         { /* per-rule points; 0 disables a rule */ }
}
```

There is no `sections` block. Sections are the solution's assemblies and a section's canonical read
DTOs come from what it exports — so nothing about a section is config's to state. A file still
carrying the key loads, and every key this version does not read is named as `removed-config-field`.
`contractsAssemblyMultiplier` (default 1) can scale surface charges declared in a satellite
`<Section>.Contracts` assembly; a weight the file sets for a retired rule is reported as
`unknown-config-weight`.

**A degraded build is refused, not scored.** If the solution doesn't compile cleanly, both
`surface-score` and `section-shape` print nothing and exit **2** (distinct from 1, so a broken tree
is machine-distinguishable from a broken tool). stderr carries the error and unresolved-reference
counts plus the individual errors with file and line. A partial score reads as authoritative and
has been quoted from broken trees before — two runs against the same broken tree agree with each
other and are wrong together, so matching totals are not evidence of soundness. Pass
`--allow-degraded` to analyze anyway; it prints the result, marks it degraded in every format, and
exits 0.

**Every section reports its size beside its score.** A score number alone can't tell you whether
a section's points fell because its API shrank or because its code did. So each group carries a
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
Cognitive complexity is the metric the implementation-shape axis actually scores; cyclomatic is carried for
continuity with the `snapshot` history series, which has always recorded it solution-wide.

The corpus is the **scoring** corpus, so a metric and a score always describe the same code: no
test projects (they are measured separately — see below), no generated code (EF
migrations, `*.g.cs`, `*.Designer.cs` — excluded from the implementation-shape axis too), and complexity
measured only over methods that have a body. `maxClassLoc` covers classes and structs — the same
set `classes` counts, which is deliberately wider than the set the `largeClass` rule scores (that
rule tracks only application services, repository implementations, controllers and background jobs).
The block describes the section's size, so it reports the section's largest class even when no rule
currently charges for it. With `--group` set, the top-level rollup
scopes to that section, the way `byRule` already does. `--list-groups` carries `locProd` for every
section — including sections that scored nothing — so sections can be ranked by size without
pulling a full report.

Metrics are informational: nothing here feeds the score, so totals are unaffected by their presence.

**Every section reports what it exports, counted.** A `surface` block per group (plus a solution
rollup) splits the corpus two ways: by visibility outside the declaring assembly (exported = public
all the way up) and by behaviour (write = the body commits persistent state or the method is
command-shaped; read otherwise, decided per method by the same detection the score uses):

```json
"surface": {
  "exported": { "types": 56, "read": 46, "write": 26,
                "typesUnusedExternally": 37, "methodsUnusedExternally": 64, "writeUnusedExternally": 25 },
  "internal": { "types": 0, "read": 2, "write": 3 }
}
```

`exported.write` is the headline: a method another assembly can call that changes state is the most
expensive surface there is, and a healthy section has very few. `*UnusedExternally` counts exported
symbols no other production project names anywhere in source. Test projects are not consulted, so a
type exported only for its tests counts as unused (`InternalsVisibleTo` is the cheaper fix), and
reflection, convention-based DI and serialization are invisible, as they are to every static
analysis. The same walk records `edges`: one `{from, to, count}` per pair of sections, the number
of times source in one names a symbol declared in the other. Informational, like `metrics`.

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

**Coverage is attributed per section when you hand over a report.** Reforge does not run tests;
`--coverage <file>` on `surface-score` and `snapshot --by-assembly` reads what the test run wrote and
joins it onto the section map by assembly name, so a coverage figure sits beside the size and
surface of exactly the code it describes:

```bash
dotnet test --collect:"XPlat Code Coverage"            # Coverlet -> TestResults/**/coverage.cobertura.xml
dotnet dotcover test --dcReportType=DetailedXML --dcOutput=coverage.xml   # JetBrains dotCover
reforge surface-score --coverage path/to/coverage.cobertura.xml
```

Both Cobertura XML (Coverlet, ReportGenerator) and dotCover DetailedXML are read, and the format is
echoed as `coverageFormat`. Each group gets `coverage: { covered, total, percent }`; the key is absent when
the report says nothing about the section, so unmeasured and 0% stay distinguishable. A section's
`.Contracts` satellite folds in as it does everywhere else. Assemblies in the report that are not in
the solution (test projects) are listed in an `info` diagnostic and left out of every column, and so
are sections the report never mentions. Line counts agree with Coverlet's own `lines-covered` /
`lines-valid`. Informational: nothing here feeds a score.

**Exact token counts, opt in.** `surface-score --count-tokens` measures each section (the sum of its
whole handwritten files, the set `metrics.files` counts, plus the same for its attributed test files)
and each top-level class (its declaration spans across every partial file, doc comments and nested
types included) with Anthropic's `count_tokens` API, using `ANTHROPIC_API_KEY`. Without the flag or
the key nothing is estimated: the `tokens` fields are absent, the size rules are skipped, and stderr
says so. Each count is the request's `input_tokens` less that of a one-character baseline
message, counted once, so file counts add up. Counts are cached by SHA-256 of model and text in
`<LocalApplicationData>/reforge/token-cache.json` (`--token-cache` overrides), so a rerun pays only
for changed text. Requests run four at a time and honour `retry-after` on 429; a failure warns on
stderr and leaves that total null and unpriced.

**Size is priced in those tokens, and only in them** (implementation-shape axis; k is thousands of
tokens). Never LOC or characters as a stand-in.

| rule | measures | free up to | linear | tax above |
|---|---|--:|--:|---|
| `sectionSize` | production and contracts files, whole | 50k | 2 per k | 50% x (k - 100)^1.5 |
| `classSize` | a top-level class, all partials summed | 8k | 5 per k | 500% x (k - 25)^1.5 |
| `testSize` | the section's test files | 100% of the section's tokens | 1 per k | none |

The rates are the weights; the thresholds and taxes are `sectionSizeFreeTokens`,
`sectionSizeTaxTokens`, `sectionSizeTaxPercent`, `classSizeFreeTokens`, `classSizeTaxTokens`,
`classSizeTaxPercent`, `sizeTaxExponent` and `testSizeFreePercent`. Whole files make the section
charge blind to partials and sibling types; only moving code to another section lowers it. Splitting
a class into siblings lowers the class charge, since each piece then fits one read. A class with a
token count skips `largeClass`, since `classSize` prices the same thing exactly; one without (no
counts, or a failed request) keeps it. On Humans the three add about 18% to the total.

```powershell
$env:ANTHROPIC_API_KEY = "..."; reforge surface-score --count-tokens --format json > tokens.json
```

`reforge section-shape` renders the same sections as a report (interfaces, DTO anchors,
cross-section use, charged read methods, advisories).

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
