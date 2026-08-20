using System.CommandLine;

namespace Reforge.Commands;

public static class SkillCommand
{
    private const string SkillText = """
        # Reforge — Roslyn Semantic Query CLI

        You have access to `reforge`, a CLI that answers code structure questions about C# solutions
        using the Roslyn semantic model. It replaces multi-round grep/read/infer cycles with single
        precise queries. Every reference, caller, implementation, and dependency is found via the
        compiler's semantic model — including references that grep misses (interface dispatch, nameof(),
        attributes, LINQ expressions).

        ## When to Use Reforge

        Use reforge instead of grep/read when you need to:
        - Find all references to a symbol (including through interfaces, nameof, attributes)
        - Understand what a class depends on or who injects it
        - Trace call chains across a solution
        - List members of a type with full signatures
        - Find method parameters matching patterns (e.g., all `bool isAdmin` params)
        - Understand type hierarchies (implementations, inheritors)

        ## Commands

        ### Finding references and callers
        ```
        reforge references <symbol>              # All references to any symbol, solution-wide
        reforge callers <method>                  # Direct callers of a method
        reforge call-chain <method> [--depth N]   # Transitive callers (default depth 5)
        ```

        ### Understanding types
        ```
        reforge members <type>                    # All members with signatures and visibility
        reforge implementations <interface>       # Types implementing an interface
        reforge inheritors <type>                 # Types deriving from a base class
        ```

        ### Dependency analysis
        ```
        reforge dependencies <class>              # What a class depends on (ctor, fields, props)
        reforge injected <type>                   # Who injects this type via constructor
        ```

        ### Usage analysis
        ```
        reforge usages <type> [--in <namespace>]  # Where a type is used, categorized by usage kind
        reforge parameters [--name X] [--type Y]  # Find parameters matching name/type patterns
        ```

        ### Service ownership analysis
        ```
        reforge dbset-usage <class>                        # Which DbSet properties a service accesses
        reforge ownership-violations --owner X --tables Y  # Who accesses tables they don't own
        reforge service-map [--namespace N]                 # Bird's-eye: each service's tables + interfaces
        ```

        ### Code health and auditing
        ```
        reforge health [--top N] [--namespace N]            # Rank types by refactoring risk (coupling, complexity, cohesion)
        reforge audit-auth                                  # Controller actions missing [Authorize] or [ValidateAntiForgeryToken]
        reforge audit-cache [--cache-method M]              # SaveChangesAsync without cache eviction
        reforge audit-immutable --types X,Y                 # Mutations on append-only entities (Remove, Update, property sets)
        reforge audit-ef                                    # EF Core pitfalls: sentinel defaults, string enums, interpolation in LINQ
        reforge audit-surface <type>                        # Per-method caller counts (prod/test); body shape for classes
        reforge audit-downstream <class>                    # Per-method outbound: dependency calls, DbSet read/write, external IO
        reforge surface-score [--group X] [--top N]         # Solution-wide score: durable surface + dependency use + internal shape
                              [--config path] [--list-groups]
                              [--format compact|markdown|json]
        ```

        ### Surface score config — `reforge.surface-score.json`

        **Sections are assemblies, not config.** A type belongs to the section of its containing
        assembly: `App.Tickets` and `App.Tickets.Contracts` are both section `Tickets` (the
        `.Contracts` assembly folds into its parent, and the dot-segment prefix shared by every
        assembly is stripped for display). Test projects are excluded. Nothing to author, nothing
        to keep in sync — the compiler enforces it.

        **Canonical read DTOs are derived, not config.** A section's published read API is the set
        of *exported* data types it declares on its contracts surface: in its `<Section>.Contracts`
        assembly, or under a `Contracts/` folder in its own assembly. A `Contracts` namespace or
        folder is not evidence on its own — an `internal` type declared there is not surface and is
        not included. A section with neither shape publishes no read API, and its score says so.

        **A satellite contracts assembly costs double.** Every surface charge on a declaration in a
        `<Section>.Contracts` assembly is multiplied by `contractsAssemblyMultiplier` (default 2).
        The same type under a `Contracts/` folder inside the section's own assembly is charged once.
        The difference is reach: the folder is only reachable by referencing the whole assembly,
        while a satellite assembly can be referenced on its own — which is the point of the shape
        and also what makes it the hardest surface to withdraw. Credits and the
        internal-complexity axis are never scaled. Each entry carries `origin` (`main` /
        `contracts`) and `multiplied`, and each group reports `mainSurfaceTotal` and
        `contractsSurfaceTotal` beside `surfaceTotal`.

        **Each group reports its size beside its score.** A `metrics` block per group (plus a
        solution rollup beside `typesAnalyzed`) carries `locProd`, `files`, `classes`,
        `interfaces`, `methods`, cognitive and cyclomatic avg/p95/max with the method holding the
        max, and `maxClassLoc` with its class. Read a score delta against it: points can fall
        because the API shrank or because the code did, and the score alone cannot tell those
        apart. The corpus is the scored corpus — no test projects, no generated code, complexity
        over methods with a body. Informational only; no metric feeds a score.

        The config file is optional, searched for upward from the solution directory, and carries
        policy only. With no file present the built-in name-pattern classifications and default
        weights still produce a full score.

        **Schema** (every key is optional — unspecified keys inherit the built-in defaults):

        ```jsonc
        {
          // Surface charges on declarations in a satellite <Section>.Contracts assembly are
          // multiplied by this. Default 2; set to 1 to price both contracts shapes the same.
          // Values <= 1 are treated as 1 — a typo weakens the rule, it never erases the surface.
          "contractsAssemblyMultiplier": 2,

          // There is no `sections` block. Sections are the solution's assemblies, their
          // canonical read DTOs are derived from what each exports from its contracts surface,
          // and their surface expectations from whether the assembly declares a repository or a
          // DbContext. A config still carrying the key is reported as `removed-config-field`.

          // Classifications tag types. A type may receive multiple tags; precedence handled by
          // the engine (e.g. on interfaces, repository/read-service tags override fullService).
          // Built-in classification names — these are exactly the names the rules read:
          //   dto, readServiceInterface, fullServiceInterface, repositoryInterface,
          //   repositoryImplementation, applicationService, controller, backgroundJob
          //
          // Declaring one REPLACES the built-in patterns for that name; it does not add to them.
          // So a block whose globs match nothing switches its rules off rather than falling back —
          // list every pattern you want, including the built-in ones you still want kept. Both
          // failure modes are reported rather than scored as zero: a declared classification that
          // matches no type is `dead-config-classification`, and a name no rule reads (a typo, or
          // an invented tag with no matching weight key) is `unknown-config-classification`.
          //
          // Each classification matches if ANY of its sub-criteria matches:
          //   namePatterns:   glob on the type's short name      (e.g. "I*ServiceRead", "*Dto")
          //   paths:          glob on the source file path       (** = any segments, * = one segment)
          //   namespaces:     prefix match on the namespace name
          //   inherits:       short-name match against any base class or implemented interface
          //   attributeNames: short-name match on attributes applied to the type (with or without "Attribute" suffix)
          "classifications": {
            "readServiceInterface": {
              "namePatterns": ["I*ServiceRead", "I*ReadService"],
              "attributeNames": ["ReadOnlyService"]
            },
            "fullServiceInterface": { "namePatterns": ["I*Service"] },
            "repositoryInterface":  { "namePatterns": ["I*Repository"], "inherits": ["IRepository"] }
          },

          // Resource ownership is DERIVED, not configured: a DbSet belongs to the section of the
          // DbContext that declares it. Any read OR write of that DbSet from a class outside the
          // owning section contributes `duplicateDbSetOwner` points to the offending class's
          // section. (Set the weight to 0 to disable the rule.)

          // Weights — every value here overrides the built-in default. Setting a weight to 0
          // disables that rule. A key that is not a rule name is reported as
          // `unknown-config-weight` rather than scored as zero — that is what a misspelling, or a
          // rule that has since been retired, looks like from inside a config.
          // Full list of rule keys (group: durable / dependency / shape):
          //
          // Durable surface:
          //   dtoScalarProperty (1), dtoCollectionProperty (2), dtoNestedProperty (3),
          //   publicDtoType (5), applicationServiceMethod (5), readServiceInterfaceMethod (6),
          //   fullServiceInterfaceMethod (8), repositoryInterfaceMethod (10),
          //   repositoryImplementationMethod (10), newRepositoryInterface (15),
          //   newRepositoryImplementation (15), diRegistration (3), controllerAction (8),
          //   backgroundJob (12), duplicateDbSetOwner (20),
          //   canonicalReadDtoReturn (-3, credit when a method returns a section's derived canonical read DTO)
          //
          // Dependency use (constructor injection across sections):
          //   sameSectionReadService (0), crossSectionReadInterface (2),
          //   crossSectionFullService (8), crossSectionRepository (25),
          //   writeCapableInterfaceUsedReadOnly (12, full-service interface paired with a read
          //     interface — via inheritance or "{Full}Read" sibling — where every observed call
          //     on the injected dep also exists on the read interface)
          //
          // Internal shape (per method):
          //   methodParameterOverflow (1, per param beyond 2), booleanParameter (3),
          //   tupleReturn (4), optionsBag (8), dashboardAdminPageName (6),
          //   oneImplementationInterface (8)
          "weights": {
            "crossSectionRepository": 25,
            "duplicateDbSetOwner": 20,
            "booleanParameter": 3
          }
        }
        ```

        **Authoring guidance for agents:**

        1. **Start with no config at all.** Sections come from the assemblies; run
           `reforge surface-score --list-groups` to see them.
        2. **Only override `classifications` if the project uses non-default name patterns.** The
           built-in defaults already match `I*Repository`, `I*Service`, `*Dto`, `*Controller`, etc.
        3. **A section that hasn't been extracted into its own assembly scores under whichever
           assembly holds it** (`Application`, `Web`, …). That's coarse on purpose; per-section
           numbers appear the moment the section becomes a project.
        4. **Weights are project-policy.** Defaults reflect a "repositories are expensive,
           cross-section calls are costly" architecture. Adjust to match how heavily the project
           penalises each pattern.
        5. **Verify by running `reforge surface-score` and inspecting the top offenders.** If the
           score is dominated by a rule that doesn't match the project's actual concerns, raise
           or lower its weight (or set it to 0) rather than redesigning the engine.

        ## Symbol Resolution

        Symbols can be specified as:
        - **Simple name:** `UserService` — matches by name, errors if ambiguous
        - **Qualified name:** `MyApp.Services.UserService` — partial or full namespace match
        - **Member access:** `UserService.GetUserAsync` — resolves type, then finds member

        When ambiguous, reforge lists all candidates with qualified names so you can disambiguate.

        ## Options

        ```
        --solution <path>          # Explicit solution path. If omitted, searches upward for .slnx/.sln
        --format <Compact|Json>    # Output format (default: Compact)
        ```

        ## Output Format

        Default output is compact, grouped by file — optimized for LLM context windows:
        ```
        3 injected of MyApp.Services.IUserService

        MyApp.Services/CachedUserService.cs
          19: CachedUserService(IUserService inner)

        MyApp.Web/Controllers/UserController.cs
          16: UserController(IUserService userService)

        MyApp.Web/Controllers/OrderController.cs
          14: OrderController(IUserService userService)
        ```

        JSON output (`--format json`) is available for programmatic consumption.

        ## Workflow Tips

        1. **Start broad, narrow down.** Use `references` or `usages` first to understand scope,
           then `callers` or `dependencies` for specific relationships.

        2. **Use qualified names** when simple names are ambiguous. The error message lists candidates.

        3. **Before renaming or moving:** Run `references <symbol>` to see every usage site.
           This catches references grep would miss.

        4. **Before modifying a method signature:** Run `callers <method>` to find every call site
           that needs updating.

        5. **To understand a class's role:** Run `dependencies` (what it uses) and `injected` (who
           uses it) to see where it fits in the dependency graph.

        6. **To find design issues:** `parameters --name isAdmin` finds privileged boolean params.
           `injected DbContext` finds classes with direct DB access.

        7. **To trace impact:** `call-chain <method>` shows the full transitive caller tree —
           how far up the stack a change propagates.

        8. **Limit results** when you only need a sample: `reforge references Foo --limit 10`
           saves context window when you don't need all 300 references.

        9. **Log feature requests** when you find yourself doing something reforge should handle:
           `reforge request "needed to find all async methods that don't pass CancellationToken"`

        ## Hot Mode (Fast Repeated Queries)

        First query pays a cold start tax (~3-20s depending on solution size). For repeated queries,
        start a hot server:

        ```
        reforge serve --solution path/to/Solution.slnx
        ```

        The server loads the workspace once and stays running. All subsequent `reforge` commands
        auto-detect the server and relay queries — dropping response time from seconds to ~200ms.

        The server writes a `.reforge-port` file in the solution directory. Commands find it
        automatically via the `--solution` path or by searching upward from the working directory.
        The server auto-reloads when source files change on disk, so you don't need to restart
        it after edits.

        Stop the server with `reforge stop` (or Ctrl+C). If the server was hard-killed and left
        a stale `.reforge-port` file behind, `reforge stop` removes it. The server also
        auto-shuts-down after 5 minutes of query inactivity to free memory; tune with
        `reforge serve --idle-timeout <minutes>` (0 disables).

        A relayed command returns the server's exit code and its stderr, so hot and cold runs are
        interchangeable. If reforge prints "a hot server is running but speaks an older protocol",
        the server predates your client: run `reforge stop` and restart `reforge serve`. Until you
        do, commands still work — they just take the slow cold path.

        ## Contributing Suggestions

        Reforge is built by AI assistants, for AI assistants. If you notice a gap — a query you
        keep doing manually that reforge should handle, output that wastes tokens, or a workflow
        that takes too many rounds — you can:

        1. `reforge request "description"` — logs it locally for the next dev session
        2. Open a GitHub issue at https://github.com/peterdrier/reforge/issues with your
           suggestion (with your owner's permission). Include what you were trying to do,
           what reforge couldn't do, and what the ideal command would look like.
        """;

    public static Command Create()
    {
        var command = new Command("skill", "Print LLM-optimized usage guide for Reforge");

        command.SetAction((parseResult, cancellationToken) =>
        {
            Console.WriteLine(SkillText);
            return Task.CompletedTask;
        });

        return command;
    }
}
