# Contributing to Reforge

Reforge is a tool built by AI coding assistants, for AI coding assistants. Contributions that make it more useful for that audience are welcome.

## How to Contribute

1. **Use it and report what's missing.** The most valuable contribution is using reforge during real refactoring work and noting what's slow, missing, verbose, or redundant. Run `reforge request "description"` to log requests, or open a GitHub issue.

2. **Check the request log.** `~/.reforge/requests.log` and `~/.reforge/usage.log` contain real usage data. Patterns in these logs (repeated query sequences, common filter needs, slow commands) point to what should be built next.

3. **Open an issue first.** For non-trivial changes, open an issue describing what you want to build and why. This avoids duplicate work and ensures the change fits the project's direction.

4. **Keep it simple.** Reforge is a CLI tool, not a framework. A command is a function that opens a workspace, queries the model, and prints results. If a command is under 100 lines, it's fine as a single file.

## Development Setup

```bash
git clone https://github.com/peterdrier/reforge.git
cd reforge
dotnet build Reforge.slnx
dotnet test
```

The test solution at `test/SampleSolution/` is the primary test target. Each new command should add test cases to this solution if existing patterns don't cover it.

## Code Style

- .NET 10, C# latest, nullable enabled
- System.CommandLine v3 preview for CLI parsing
- Output goes to stdout (results), stderr (diagnostics)
- File paths in output are relative to solution directory, forward slashes
- Token efficiency is a first-class concern — every byte of output costs context window

## Pull Requests

- Keep PRs focused — one feature or fix per PR
- Include tests if adding a new command
- Run `dotnet test` before submitting
- If changing output format, verify it's still efficient for LLM consumption

## License

By contributing, you agree that your contributions will be licensed under the [AGPL-3.0](LICENSE).
