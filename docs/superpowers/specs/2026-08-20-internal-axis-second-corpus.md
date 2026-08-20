# Internal-axis signals — the second corpus, and the delta form

## Status

Measurement record (2026-08-20). Addendum to `2026-08-19-internal-axis-signal-measurements.md`,
closing the two gaps that document names in "What was not measured, and why":

> - **A second corpus.** Every number is from Humans. Reforge itself is 1 section and 107 types with
>   no DTOs and no repositories, so it cannot exercise these signals […]
> - **Net-new single-caller helpers.** Requires two runs across a real change […]

Both are now measured. Three findings:

1. **The dismissal of reforge as a second corpus is half wrong.** It cannot exercise feature envy —
   that much holds. It exercises single-caller helpers *harder than Humans does*: 43% of method LOC
   against Humans' 13%.
2. **The delta form of Signal A is rejected too.** Newly-added private methods are single-caller at
   73–81%, which is the stock rate. A rule charging net-new single-caller helpers charges three of
   every four private methods a change adds — a size rule with extra steps.
3. **One form has never been measured and is not rejected:** the per-section *share*. It spans 0.5%
   to 37.3% across 42 Humans sections, a 70× range. Recorded as a candidate, not recommended — see
   the caveat, which is that its top hit is a section where the shape is correct style.

Nothing here proposes a weight.

## Method

Same treatment as the harness the 2026-08-19 spec describes: a throwaway Roslyn console project
(~250 lines, not committed), one document walk per non-test project, excluding `obj/`, `bin/`,
`Migrations/`, `*.g.cs`, `*.Designer.cs`, `*.generated.cs`.

| | |
|---|---|
| Humans | `283510e`, 67 projects, 7,601 methods, 112,125 method LOC, 1,388 private methods |
| Reforge | `2dec9af` and five earlier commits back to `ae6cee0` (2026-05-30) |
| SDK | .NET 10.0.111 |
| Humans score | `surfaceTotal` 16,689, `internalComplexityTotal` 3,038, combined 19,727, `degraded: false` |

The Signal A predicate here is simpler than the 2026-08-19 one — `private`, has a body, distinct
enclosing members as callers — and does **not** exclude explicit interface implementations or
`nameof` mentions. It reproduces that document's stock base rate to within 0.3 points (59.5% here vs
59.2% there), which is the check that matters: the two harnesses agree on the same corpus, so the
numbers below can be read against that document's.

Humans is a shallow clone (4 commits), so its history is not measurable from here. Reforge's is —
66 commits over 82 days — and it is the population #19 is actually about, being LLM-written
end to end.

## Gap 1 — the second corpus

| | private | single-caller | share | helper LOC | method LOC | LOC share |
|---|---:|---:|---:|---:|---:|---:|
| Humans (human + LLM) | 1,388 | 826 | 59% | 15,679 | 112,125 | **13%** |
| Reforge (LLM end to end) | 306 | 246 | 80% | 5,492 | 12,714 | **43%** |

Reforge carries 3.3× Humans' single-caller-helper LOC share. The 2026-08-19 spec's reason for
setting reforge aside — 1 section, no DTOs, no repositories — is a statement about the *surface*
axis. Signal A needs none of those things; it needs private methods, and reforge has 306.

That the two corpora differ this much on a signal neither was tuned against is the first evidence
that it measures something about *how the code was written* rather than how much of it there is.

## Gap 2 — the delta form

The 2026-08-19 spec confirms Signal A "as a delta signal" and is explicit that the successor was not
itself measured. Six commits across reforge's history:

| commit | date | methods | method LOC | private | single-caller | share | LOC share |
|---|---|---:|---:|---:|---:|---:|---:|
| `ae6cee0` | 05-30 | 306 | 9,069 | 205 | 169 | 82% | 44% |
| `cc48bbf` | 06-06 | 329 | 9,609 | 221 | 182 | 82% | 41% |
| `c8b49b5` | 08-13 | 337 | 9,782 | 227 | 188 | 82% | 42% |
| `324f495` | 08-15 | 369 | 10,501 | 237 | 196 | 82% | 39% |
| `0cc1128` | 08-19 | 389 | 10,953 | 251 | 206 | 82% | 39% |
| `2dec9af` | 08-20 | 458 | 12,714 | 306 | 246 | 80% | 43% |

The share does not move. Over 82 days and +101 private methods it sits at 80–82% throughout.

The marginal rate — of the private methods a stretch of history *added*, how many are single-caller —
is the number the delta rule would charge:

| span | private added | single-caller added | marginal rate |
|---|---:|---:|---:|
| `ae6cee0` → `cc48bbf` | +16 | +13 | 81% |
| `0cc1128` → `2dec9af` | +55 | +40 | 73% |
| `ae6cee0` → `2dec9af` | +101 | +77 | 76% |

**The marginal rate is the stock rate.** A delta-native rule charging net-new single-caller helpers
would fire on roughly three of every four private methods a change adds, in proportion to how much
the change adds — which is the defining property of the size rules #19 exists to get away from.

This does not disprove that linter-driven extraction is *distinguishable* from intended
decomposition. It shows that the population a delta rule would charge is not predominantly the
former, on the one corpus where the whole history is LLM-written. The 2026-08-19 audit already
suspected the distinction "may not be decidable from structure at all"; this is the quantitative
version of that suspicion.

### Consequence for the size rules

#19's premise is that `longMethod` and `cognitiveComplexity` are gameable by extraction and so
should be reweighted or retired, with a single-caller-helper rule as the counterweight. With both
the stock and delta forms of the counterweight rejected, the premise needs restating rather than
implementing:

- Extraction satisfies `longMethod`. **Inlining** satisfies a single-caller-helper rule. Neither is
  independently safe.
- The only edit that satisfies both at once is giving a helper a second real caller, or deleting
  duplicated logic. That is reuse, which is what `CLAUDE.md` says the tool exists to encourage.
- So the pairing is sound in principle and the measurement says nothing against it. What the
  measurement says is that **the counterweight cannot be a per-helper charge in either form** — the
  base rate is too high for a stock charge and the marginal rate is too high for a delta charge.

## The form nobody has measured — per-section share

Single-caller helper LOC as a percentage of the section's production LOC, Humans, 42 sections:

| | section | share |
|---|---|---:|
| top | Analyzers | 37.3% |
| | Stripe | 33.9% |
| | TicketTailor | 26.4% |
| | Search | 25.5% |
| | Monitor | 22.7% |
| | Agent | 20.4% |
| bottom | Auth | 2.6% |
| | Governance | 1.0% |
| | Email | 0.5% |

A 70× spread, and reforge at 43% sits above every Humans section. A rule of this shape charges a
section for a property of its *shape* rather than charging each helper, so extraction raises the
charge instead of lowering it — the anti-gaming direction #19's Gate 1 asks for.

**Not recommended, and specifically not on the strength of that spread.** The top hit is
`Humans.Analyzers`, where decomposing into many small single-caller rule methods is the correct style
for the domain — one method per diagnostic, called once from a registration. If the loudest hit is
correct code, the signal may be measuring "small-method-shaped" rather than "fragmented", which is
the same failure the duplication rule died of. Settling that needs a manual read of the top three
sections' helpers, which this round did not do.

## Incidental

**Uncalled private methods: 32 on Humans, 245 LOC.** Dead code, found for free by the same pass.
Too small and too unambiguous to score — a `reforge dead-private` diagnostic at most, and only if
someone asks for it.

## What is still not measured

- Whether the per-section share separates fragmentation from correct small-method style. Needs the
  manual read named above.
- Feature envy on a second corpus. Reforge cannot supply it: 0 DTO anchors, and the 2026-08-19
  refinement needs entity/DTO classification the tool does not have.
- Whether any of this changes with a corpus that has been through a scored refactor. Nothing has
  been refactored *to satisfy reforge* yet, so every number here is the pre-scoring baseline. The
  fragmentation #19 fears is a predicted response to the tool, and the response cannot be measured
  before the tool is used that way. `self-score.yml` makes it observable from here on.
