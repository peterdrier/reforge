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
        ```

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
        Kill the server with Ctrl+C to clean up. The server auto-reloads when source files
        change on disk, so you don't need to restart it after edits.

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
