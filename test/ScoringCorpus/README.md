# Scoring corpus

Small before/after pairs that pin down what `surface-score` should say about a change.

- `Pairs/<Id>/before` and `Pairs/<Id>/after` each compile as one project. A subdirectory (such as
  `Camp/`) is a separate project in its own section, referenced by the main one.
- `manifest.json` labels each pair: `better` (after scores lower), `worse` (after scores higher) or
  `lateral` (after scores exactly the same; totals are integers, so no tolerance). A `gap` names the
  root cause of a pair that fails today.
- `ScoringCorpusTests` scores every pair under the default config. It fails when an unmarked pair
  fails or a gap pair starts passing, so closing a gap means deleting its marker.

Run it and write the results table:

```bash
REFORGE_CORPUS_REPORT=/tmp/corpus.md dotnet test test/Reforge.Tests --filter ScoringCorpusTests
```

`REFORGE_CORPUS_CONFIG=<config.json>` scores under other weights (gap markers are not checked then),
which makes the corpus a fitness function for a weight sweep. `REFORGE_CORPUS_ENTRIES=<Id>,<Id>`
appends every scored entry for those pairs.
