#!/usr/bin/env python3
"""Render a Reforge self-score delta as markdown.

Reads two `surface-score --format json` payloads for the same solution — one from the PR's base
tree, one from its head — and prints a report of what moved.

Solution totals only. Reforge is one section, so a per-section breakdown would be the same numbers
under another heading; the section columns exist for multi-section corpora like Humans.

Size and complexity rows always print, delta or not. A metrics table that vanishes when nothing
moved is indistinguishable from a pass that broke: the absolute numbers are the point, and the
delta column is the annotation. Only the rule rows are delta-filtered, because there the list of
rules that did not fire is long and says nothing.
"""

import json
import sys

MARKER = "<!-- reforge-self-score -->"


def signed(delta, places=0):
    """A delta as a signed string, or an em dash when it is zero."""
    if places:
        if abs(delta) < 10 ** -places / 2:
            return "—"
        return f"{delta:+.{places}f}"
    return "—" if delta == 0 else f"{delta:+d}"


def row(label, base, head, places=0):
    fmt = (lambda v: f"{v:.{places}f}") if places else (lambda v: f"{v:,}")
    return f"| {label} | {fmt(base)} | {fmt(head)} | {signed(head - base, places)} |"


def named(value, name):
    """A max-valued metric with the symbol it came from, when the tool named one."""
    return f"{value:,} ({name})" if name else f"{value:,}"


def main():
    if len(sys.argv) < 3:
        sys.exit("usage: self-score-report.py <base.json> <head.json> [base-sha] [head-sha]")

    with open(sys.argv[1]) as f:
        base = json.load(f)
    with open(sys.argv[2]) as f:
        head = json.load(f)
    base_sha = sys.argv[3][:7] if len(sys.argv) > 3 else "base"
    head_sha = sys.argv[4][:7] if len(sys.argv) > 4 else "head"

    bm, hm = base["metrics"], head["metrics"]
    bt, ht = base["tests"], head["tests"]

    # Both key spellings: the axis was renamed, and a baseline JSON can predate the rename.
    def shape(r):
        return r.get("implementationShapeTotal", r.get("internalComplexityTotal", 0))

    out = [
        MARKER,
        "## Reforge self-score",
        "",
        f"`Reforge.slnx` at `{base_sha}` vs `{head_sha}`, both scored with **this PR's build** of the "
        "tool — so the deltas below are the code changing, not the rules.",
        "",
        "### Score",
        "",
        "| Axis | Base | Head | Δ |",
        "|---|---:|---:|---:|",
        row("Surface", base["surfaceTotal"], head["surfaceTotal"]),
        row("Implementation shape", shape(base), shape(head)),
        row("**Total**", base["total"], head["total"]),
        row("Types analyzed", base["typesAnalyzed"], head["typesAnalyzed"]),
        "",
        "### Corpus size & complexity",
        "",
        "| Metric | Base | Head | Δ |",
        "|---|---:|---:|---:|",
        row("Production LOC", bm["locProd"], hm["locProd"]),
        row("Files", bm["files"], hm["files"]),
        row("Classes", bm["classes"], hm["classes"]),
        row("Interfaces", bm["interfaces"], hm["interfaces"]),
        row("Methods", bm["methods"], hm["methods"]),
        row("Cognitive avg", bm["cognitive"]["avg"], hm["cognitive"]["avg"], places=2),
        row("Cognitive p95", bm["cognitive"]["p95"], hm["cognitive"]["p95"]),
        row("Cognitive max", bm["cognitive"]["max"], hm["cognitive"]["max"]),
        row("Cyclomatic avg", bm["cyclomatic"]["avg"], hm["cyclomatic"]["avg"], places=2),
        row("Cyclomatic p95", bm["cyclomatic"]["p95"], hm["cyclomatic"]["p95"]),
        row("Cyclomatic max", bm["cyclomatic"]["max"], hm["cyclomatic"]["max"]),
        row("Max class LOC", bm["maxClassLoc"], hm["maxClassLoc"]),
        "",
        f"Worst cognitive: {named(hm['cognitive']['max'], hm['cognitive']['maxMethod'])} · "
        f"worst cyclomatic: {named(hm['cyclomatic']['max'], hm['cyclomatic']['maxMethod'])} · "
        f"largest class: {named(hm['maxClassLoc'], hm['maxClassLocName'])}",
        "",
        "### Test corpus",
        "",
        "| Metric | Base | Head | Δ |",
        "|---|---:|---:|---:|",
        row("Test LOC", bt["loc"], ht["loc"]),
        row("Test files", bt["files"], ht["files"]),
        row("Test projects", bt["projects"], ht["projects"]),
        row("Test LOC % of production", bt["locVsProdPercent"], ht["locVsProdPercent"]),
        "",
        "### Rules that moved",
        "",
    ]

    br, hr = base["byRule"], head["byRule"]
    moved = sorted(
        (r for r in set(br) | set(hr) if hr.get(r, 0) != br.get(r, 0)),
        key=lambda r: (-abs(hr.get(r, 0) - br.get(r, 0)), r),
    )
    if moved:
        out += ["| Rule | Base | Head | Δ |", "|---|---:|---:|---:|"]
        out += [row(r, br.get(r, 0), hr.get(r, 0)) for r in moved]
    else:
        out.append("No rule total changed.")

    for label, payload in (("base", base), ("head", head)):
        if payload["build"]["degraded"]:
            out += ["", f"> ⚠️ The {label} workspace was degraded — treat its numbers as unreliable."]

    print("\n".join(out))


if __name__ == "__main__":
    main()
