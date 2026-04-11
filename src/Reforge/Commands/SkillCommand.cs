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

        ## Symbol Resolution

        Symbols can be specified as:
        - **Simple name:** `UserService` — matches by name, errors if ambiguous
        - **Qualified name:** `MyApp.Services.UserService` — partial or full namespace match
        - **Member access:** `UserService.GetUserAsync` — resolves type, then finds member

        When ambiguous, reforge lists all candidates with qualified names so you can disambiguate.

        ## Options

        ```
        --solution <path>    # Explicit solution path. If omitted, searches upward for .slnx/.sln
        --format <Json|Table> # Output format (default: Json)
        ```

        ## Output Format

        JSON output (default) is structured for programmatic consumption:
        ```json
        {
          "command": "references",
          "symbol": "MyApp.Services.UserService",
          "results": [
            {
              "file": "src/Controllers/HomeController.cs",
              "line": 15,
              "column": 22,
              "context": "private readonly UserService _service;",
              "containingSymbol": "HomeController"
            }
          ],
          "total": 1
        }
        ```

        Table output (`--format table`) is one line per result:
        ```
        src/Controllers/HomeController.cs:15  HomeController  private readonly UserService _service;
        ```

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
