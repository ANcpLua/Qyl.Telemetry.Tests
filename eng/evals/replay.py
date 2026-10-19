#!/usr/bin/env python3
"""Replay the qyl-investigate reviewer prompts through `claude -p` against mcp.qyl.at and score them.

Each case in evals/qyl-investigate/cases/*.json is run `--runs` times. Every run is one
`claude -p` child with only the `qyl` MCP server (the sign-in stored for server name `qyl`
on this Mac decides the project; the reviewer sign-in reaches openai-review) and the qyl
plugin loaded from `--plugin-dir`, so the qyl-investigate skill is the one under test. The
stream-json output gives the tool calls, the final answer, the tokens and the cost.

Scores per run, each 0..1:
  tools     expected steps satisfied (each step is a set of acceptable tools; any one counts),
            0 when a forbidden tool was called or an argument check failed
  mentions  share of the case's must_mention patterns found in the final answer
  judge     optional LLM grade of the final answer against the case's rubric (--judge)
  score     mean of the components that ran

Usage:
  python3 -I eng/evals/replay.py [--case ID ...] [--tag TAG ...] [--runs N] [--model M]
      [--plugin-dir DIR] [--variant SPEC.json] [--label NAME] [--judge] [--judge-model M]
      [--max-cost-usd X] [--dry-run]

Outputs under evals/qyl-investigate/results/<timestamp>-<label>/:
  <case>-<run>.jsonl   the raw stream
  <case>-<run>.json    the parsed run: tool calls, answer, usage, cost, scores
  summary.json         per-case means, for compare.py
  summary.md           the same as a table
"""
from __future__ import annotations

import argparse
import datetime as dt
import glob
import json
import os
import pathlib
import re
import shutil
import statistics
import subprocess
import sys
import tempfile

ROOT = pathlib.Path(__file__).resolve().parents[2]
EVAL_DIR = ROOT / "evals" / "qyl-investigate"
MCP_URL = "https://mcp.qyl.at/mcp"


def load_json(path: pathlib.Path) -> dict:
    with path.open("r", encoding="utf-8") as handle:
        return json.load(handle)


def installed_plugin_dir() -> pathlib.Path:
    candidates = sorted(glob.glob(os.path.expanduser("~/.claude/plugins/cache/ancplua-anthropic/qyl/*/")))
    if not candidates:
        sys.exit("no installed qyl plugin under ~/.claude/plugins/cache/ancplua-anthropic/qyl; pass --plugin-dir")
    return pathlib.Path(candidates[-1])


def apply_variant(source: pathlib.Path, spec: dict, scratch: pathlib.Path) -> pathlib.Path:
    """Copy the plugin and apply the spec's replacements, each of which must match exactly once."""
    target = scratch / f"qyl-{spec.get('name', 'variant')}"
    shutil.copytree(source, target, ignore=shutil.ignore_patterns("node_modules", ".git"))
    for replacement in spec["replacements"]:
        path = target / replacement["file"]
        text = path.read_text(encoding="utf-8")
        count = text.count(replacement["replace"])
        if count != 1:
            sys.exit(f"variant {spec.get('name')}: {replacement['file']} has {count} matches for the line to replace")
        path.write_text(text.replace(replacement["replace"], replacement["with"]), encoding="utf-8")
    return target


def claude_command(case: dict, args: argparse.Namespace, mcp_config: pathlib.Path, plugin_dir: pathlib.Path | None,
                   server: str) -> list[str]:
    return [
        "claude", "-p", case["prompt"],
        "--model", args.model,
        "--strict-mcp-config", "--mcp-config", str(mcp_config),
        "--setting-sources", "project",
        *([] if plugin_dir is None else ["--plugin-dir", str(plugin_dir)]),
        "--allowedTools", f"mcp__{server}", "ToolSearch", "Skill",
        "--max-turns", str(case.get("max_turns", 12)),
        "--no-session-persistence",
        "--output-format", "stream-json", "--verbose",
    ]


def parse_stream(path: pathlib.Path, server: str) -> dict:
    prefix = f"mcp__{server}__"
    run = {"tool_calls": [], "tool_errors": 0, "answer": "", "mcp_connected": None, "model": None,
           "cost_usd": None, "turns": None, "duration_ms": None, "tokens": {"input": 0, "cache_write": 0, "cache_read": 0, "output": 0},
           "result_subtype": None}
    with path.open("r", encoding="utf-8") as handle:
        for line in handle:
            line = line.strip()
            if not line:
                continue
            try:
                row = json.loads(line)
            except json.JSONDecodeError:
                continue
            kind = row.get("type")
            if kind == "system" and row.get("subtype") == "init":
                run["model"] = row.get("model")
                servers = {s.get("name"): s.get("status") for s in row.get("mcp_servers", [])}
                run["mcp_connected"] = servers.get(server) == "connected"
            elif kind == "assistant":
                for block in row.get("message", {}).get("content", []):
                    if block.get("type") == "tool_use" and str(block.get("name", "")).startswith(prefix):
                        run["tool_calls"].append({"tool": block["name"][len(prefix):], "input": block.get("input", {})})
            elif kind == "user":
                for block in row.get("message", {}).get("content", []):
                    if isinstance(block, dict) and block.get("type") == "tool_result" and block.get("is_error"):
                        run["tool_errors"] += 1
            elif kind == "result":
                run["result_subtype"] = row.get("subtype")
                run["answer"] = str(row.get("result") or "")
                run["cost_usd"] = row.get("total_cost_usd")
                run["turns"] = row.get("num_turns")
                run["duration_ms"] = row.get("duration_ms")
                usage = row.get("usage") or {}
                run["tokens"] = {
                    "input": usage.get("input_tokens", 0),
                    "cache_write": usage.get("cache_creation_input_tokens", 0),
                    "cache_read": usage.get("cache_read_input_tokens", 0),
                    "output": usage.get("output_tokens", 0),
                }
    return run


def argument_check_passes(check: dict, calls: list[dict]) -> bool:
    matching = [call for call in calls if call["tool"] == check["tool"]]
    if not matching:
        return False
    for call in matching:
        value = call["input"].get(check["argument"])
        if isinstance(value, (int, float)) and ("gte" not in check or value >= check["gte"]):
            return True
        if any(key in call["input"] for key in check.get("or_present", [])):
            return True
    return False


def score_tools(expect: dict, names: dict, calls: list[dict]) -> tuple[float, list[str]]:
    called = {call["tool"] for call in calls}
    notes: list[str] = []
    forbidden = {names["tools"][tool] for tool in expect.get("forbidden", []) if tool in names["tools"]}
    if called & forbidden:
        notes.append(f"forbidden tool called: {', '.join(sorted(called & forbidden))}")
        return 0.0, notes
    steps = expect.get("tools", [])
    satisfied = 0
    for step in steps:
        accepted = {names["tools"][tool] for tool in step if tool in names["tools"]}
        if called & accepted:
            satisfied += 1
        else:
            notes.append(f"missing step: one of {', '.join(step)}")
    bound_calls = [{"tool": call["tool"], "input": call["input"]} for call in calls]
    for check in expect.get("argument_checks", []):
        bound = {**check, "tool": names["tools"].get(check["tool"], check["tool"]),
                 "argument": names["arguments"].get(check["argument"], check["argument"])}
        if not argument_check_passes(bound, bound_calls):
            notes.append(f"argument check failed: {check['tool']}.{check['argument']}")
            return 0.0, notes
    return (satisfied / len(steps)) if steps else 1.0, notes


def score_mentions(expect: dict, answer: str) -> tuple[float, list[str]]:
    patterns = expect.get("must_mention", [])
    if not patterns:
        return 1.0, []
    missing = [pattern for pattern in patterns if not re.search(pattern, answer, re.IGNORECASE)]
    return (len(patterns) - len(missing)) / len(patterns), [f"not mentioned: {pattern}" for pattern in missing]


def judge(expect: dict, answer: str, model: str) -> tuple[float | None, str]:
    """Grade the final answer against the rubric with a second, cheaper model; returns (0..1, reason)."""
    prompt = (
        "You grade one answer an agent gave to a telemetry question. Grade only against the rubric; "
        "do not use outside knowledge. Reply with one JSON object and nothing else: "
        '{"score": <0, 0.25, 0.5, 0.75 or 1>, "reason": "<one sentence>"}.\n\n'
        f"Rubric:\n{expect.get('rubric', '')}\n\nAnswer:\n{answer}\n"
    )
    completed = subprocess.run(
        ["claude", "-p", prompt, "--model", model, "--strict-mcp-config", "--setting-sources", "project",
         "--disallowedTools", "*", "--max-turns", "1", "--no-session-persistence", "--output-format", "json"],
        capture_output=True, text=True, stdin=subprocess.DEVNULL, check=False,
    )
    try:
        text = json.loads(completed.stdout).get("result", "")
        match = re.search(r"\{.*\}", text, re.DOTALL)
        verdict = json.loads(match.group(0)) if match else {}
        score = float(verdict.get("score"))
        return max(0.0, min(1.0, score)), str(verdict.get("reason", ""))
    except (ValueError, TypeError, json.JSONDecodeError):
        return None, f"judge gave no verdict: {completed.stdout[:200]!r} {completed.stderr[:200]!r}"


def mean(values: list[float]) -> float | None:
    return round(statistics.fmean(values), 4) if values else None


def summarize(case_runs: dict[str, list[dict]], meta: dict) -> dict:
    cases = {}
    for case_id, runs in case_runs.items():
        scored = [run for run in runs if run["result_subtype"] == "success"]
        cases[case_id] = {
            "runs": len(runs),
            "succeeded": len(scored),
            "score": mean([run["scores"]["score"] for run in scored]),
            "tools": mean([run["scores"]["tools"] for run in scored]),
            "mentions": mean([run["scores"]["mentions"] for run in scored]),
            "judge": mean([run["scores"]["judge"] for run in scored if run["scores"]["judge"] is not None]),
            "tool_calls": mean([len(run["tool_calls"]) for run in scored]),
            "tool_errors": mean([run["tool_errors"] for run in scored]),
            "turns": mean([run["turns"] for run in scored if run["turns"] is not None]),
            "input_tokens": mean([run["tokens"]["input"] + run["tokens"]["cache_write"] + run["tokens"]["cache_read"] for run in scored]),
            "output_tokens": mean([run["tokens"]["output"] for run in scored]),
            "cost_usd": mean([run["cost_usd"] for run in scored if run["cost_usd"] is not None]),
            "duration_s": mean([run["duration_ms"] / 1000 for run in scored if run["duration_ms"] is not None]),
            "tools_called": sorted({call["tool"] for run in scored for call in run["tool_calls"]}),
        }
    scores = [case["score"] for case in cases.values() if case["score"] is not None]
    costs = [case["cost_usd"] * case["runs"] for case in cases.values() if case["cost_usd"] is not None]
    return {**meta, "cases": cases, "overall": {"score": mean(scores), "total_cost_usd": round(sum(costs), 4) if costs else None}}


def markdown(summary: dict) -> str:
    lines = [f"# qyl-investigate replay: {summary['label']}", "",
             f"model {summary['model']}, plugin {summary['plugin_dir']}, bound to {summary['bound_to']}, {summary['started']}", "",
             "| Case | Runs | Score | Tools | Mentions | Judge | Calls | Turns | Input tok | Output tok | Cost USD | Seconds |",
             "|---|---|---|---|---|---|---|---|---|---|---|---|"]
    for case_id, case in summary["cases"].items():
        cell = lambda value: "n/a" if value is None else (f"{value:.2f}" if isinstance(value, float) else str(value))  # noqa: E731
        lines.append(f"| {case_id} | {case['succeeded']}/{case['runs']} | {cell(case['score'])} | {cell(case['tools'])} | {cell(case['mentions'])} | "
                     f"{cell(case['judge'])} | {cell(case['tool_calls'])} | {cell(case['turns'])} | {cell(case['input_tokens'])} | "
                     f"{cell(case['output_tokens'])} | {cell(case['cost_usd'])} | {cell(case['duration_s'])} |")
    overall = summary["overall"]
    lines += ["", f"Overall score {overall['score']}, total cost {overall['total_cost_usd']} USD."]
    return "\n".join(lines) + "\n"


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--case", action="append", default=[], help="case id to run (repeatable); default: every case tagged read")
    parser.add_argument("--tag", action="append", default=[], help="run cases carrying this tag (repeatable); default: read")
    parser.add_argument("--runs", type=int, default=3)
    parser.add_argument("--model", default="sonnet")
    parser.add_argument("--plugin-dir", help="plugin to load; default: the installed qyl plugin cache")
    parser.add_argument("--no-plugin", action="store_true",
                        help="load no plugin: the connector as a claude.ai reviewer sees it, server instructions and tool descriptions only")
    parser.add_argument("--variant", help="A/B spec (evals/qyl-investigate/variants/*.json) applied to a copy of the plugin")
    parser.add_argument("--label", help="results directory suffix; default: variant name or baseline")
    parser.add_argument("--judge", action="store_true", help="also grade each answer against the rubric with --judge-model")
    parser.add_argument("--judge-model", default="haiku")
    parser.add_argument("--max-cost-usd", type=float, help="stop launching runs once the recorded cost passes this")
    parser.add_argument("--dry-run", action="store_true", help="print the commands and exit")
    args = parser.parse_args()

    names = load_json(EVAL_DIR / "tool-names.json")
    server = names["server"]
    tags = set(args.tag or ["read"])
    cases = [load_json(path) for path in sorted((EVAL_DIR / "cases").glob("*.json"))]
    cases = [case for case in cases if (case["id"] in args.case) or (not args.case and tags & set(case.get("tags", [])))]
    if not cases:
        sys.exit("no case matched")

    scratch = pathlib.Path(tempfile.mkdtemp(prefix="qyl-eval-"))
    plugin_dir = None if args.no_plugin else (pathlib.Path(args.plugin_dir).resolve() if args.plugin_dir else installed_plugin_dir())
    label = args.label or ("no-plugin" if args.no_plugin else "baseline")
    if args.variant:
        if plugin_dir is None:
            sys.exit("--variant needs a plugin to change; drop --no-plugin")
        spec = load_json(pathlib.Path(args.variant))
        plugin_dir = apply_variant(plugin_dir, spec, scratch)
        label = args.label or spec.get("name", "variant")
    mcp_config = scratch / "mcp.json"
    mcp_config.write_text(json.dumps({"mcpServers": {server: {"type": "http", "url": MCP_URL}}}), encoding="utf-8")

    started = dt.datetime.now(dt.timezone.utc)
    out_dir = EVAL_DIR / "results" / f"{started.strftime('%Y%m%dT%H%M%SZ')}-{label}"
    if args.dry_run:
        for case in cases:
            print(" ".join(repr(part) if " " in part else part for part in claude_command(case, args, mcp_config, plugin_dir, server)))
        return 0
    out_dir.mkdir(parents=True)

    spent = 0.0
    case_runs: dict[str, list[dict]] = {}
    for case in cases:
        for index in range(1, args.runs + 1):
            if args.max_cost_usd is not None and spent >= args.max_cost_usd:
                print(f"cost ceiling {args.max_cost_usd} USD reached after {spent:.2f}; stopping", file=sys.stderr)
                break
            stream = out_dir / f"{case['id']}-{index}.jsonl"
            with stream.open("w", encoding="utf-8") as handle:
                subprocess.run(claude_command(case, args, mcp_config, plugin_dir, server), stdout=handle,
                               stderr=subprocess.PIPE, stdin=subprocess.DEVNULL, text=True, check=False)
            run = parse_stream(stream, server)
            tools_score, notes = score_tools(case["expect"], names, run["tool_calls"])
            mentions_score, mention_notes = score_mentions(case["expect"], run["answer"])
            judge_score, judge_reason = (judge(case["expect"], run["answer"], args.judge_model) if args.judge and run["answer"] else (None, ""))
            components = [tools_score, mentions_score] + ([judge_score] if judge_score is not None else [])
            run["scores"] = {"tools": tools_score, "mentions": mentions_score, "judge": judge_score,
                             "score": round(statistics.fmean(components), 4), "notes": notes + mention_notes,
                             "judge_reason": judge_reason}
            run["case"] = case["id"]
            run["run"] = index
            (out_dir / f"{case['id']}-{index}.json").write_text(json.dumps(run, indent=2), encoding="utf-8")
            case_runs.setdefault(case["id"], []).append(run)
            spent += run["cost_usd"] or 0.0
            status = "ok" if run["result_subtype"] == "success" else f"FAILED ({run['result_subtype']}, mcp connected: {run['mcp_connected']})"
            print(f"{case['id']} run {index}: {status}; score {run['scores']['score']}; tools {[call['tool'] for call in run['tool_calls']]}; "
                  f"cost {run['cost_usd']}; notes {run['scores']['notes']}")

    summary = summarize(case_runs, {"label": label, "model": args.model, "plugin_dir": "none" if plugin_dir is None else str(plugin_dir),
                                    "variant": args.variant, "bound_to": names["bound_to"], "started": started.isoformat(timespec="seconds"),
                                    "judge": args.judge_model if args.judge else None})
    (out_dir / "summary.json").write_text(json.dumps(summary, indent=2), encoding="utf-8")
    (out_dir / "summary.md").write_text(markdown(summary), encoding="utf-8")
    print(markdown(summary))
    print(f"results: {out_dir}")
    shutil.rmtree(scratch, ignore_errors=True)
    return 0


if __name__ == "__main__":
    sys.exit(main())
