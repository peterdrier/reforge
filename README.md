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
