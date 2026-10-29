---
name: reforge
description: Roslyn-powered semantic query CLI for C# solutions. Use when you need to find references, callers, implementations, dependencies, members, or trace call chains in a C# codebase — replaces multi-round grep/read cycles with single precise queries.
---

Run `reforge skill` to get the full usage guide:

```bash
reforge skill
```

Follow the output as your reference for which command to use and how to interpret results.

If `reforge` is not installed as a global tool, install it from the repo checkout:

```bash
dotnet pack <repo>/src/Reforge -o <repo>/src/Reforge/nupkg
dotnet tool install --global --add-source <repo>/src/Reforge/nupkg Reforge
```
