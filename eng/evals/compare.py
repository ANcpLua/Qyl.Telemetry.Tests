#!/usr/bin/env python3
"""Compare two qyl-investigate replay summaries, A against B, case by case.

Usage:
  python3 -I eng/evals/compare.py <A results dir or summary.json> <B results dir or summary.json>

Prints a table of B minus A for the score, the tool calls, the tokens and the cost of each case,
then the overall deltas. Use it to measure one changed skill or instruction line: run replay.py
once for the installed plugin and once with --variant, then compare the two result directories.
"""
from __future__ import annotations

import json
import pathlib
import sys

FIELDS = [("score", "Score"), ("tools", "Tools"), ("mentions", "Mentions"), ("judge", "Judge"),
          ("tool_calls", "Calls"), ("turns", "Turns"), ("input_tokens", "Input tok"),
          ("output_tokens", "Output tok"), ("cost_usd", "Cost USD"), ("duration_s", "Seconds")]


def load(argument: str) -> dict:
    path = pathlib.Path(argument)
    if path.is_dir():
        path = path / "summary.json"
    with path.open("r", encoding="utf-8") as handle:
        return json.load(handle)


def cell(value) -> str:
    if value is None:
        return "n/a"
    return f"{value:+.2f}" if isinstance(value, float) else str(value)


def delta(a, b):
    if a is None or b is None:
        return None
    return round(b - a, 4)


def main() -> int:
    if len(sys.argv) != 3:
        print(__doc__, file=sys.stderr)
        return 2
    a, b = load(sys.argv[1]), load(sys.argv[2])
    print(f"A: {a['label']} ({a['model']}, {a['started']})")
    print(f"B: {b['label']} ({b['model']}, {b['started']})")
    print()
    print("| Case | " + " | ".join(f"{title} A → B (Δ)" for _, title in FIELDS) + " |")
    print("|---|" + "---|" * len(FIELDS))
    for case_id in sorted(set(a["cases"]) | set(b["cases"])):
        row_a, row_b = a["cases"].get(case_id, {}), b["cases"].get(case_id, {})
        cells = []
        for key, _ in FIELDS:
            va, vb = row_a.get(key), row_b.get(key)
            show = lambda v: "n/a" if v is None else (f"{v:.2f}" if isinstance(v, float) else str(v))  # noqa: E731
            cells.append(f"{show(va)} → {show(vb)} ({cell(delta(va, vb))})")
        print(f"| {case_id} | " + " | ".join(cells) + " |")
    print()
    print(f"Overall score: {a['overall']['score']} → {b['overall']['score']} ({cell(delta(a['overall']['score'], b['overall']['score']))}); "
          f"total cost: {a['overall']['total_cost_usd']} → {b['overall']['total_cost_usd']} USD "
          f"({cell(delta(a['overall']['total_cost_usd'], b['overall']['total_cost_usd']))})")
    return 0


if __name__ == "__main__":
    sys.exit(main())
