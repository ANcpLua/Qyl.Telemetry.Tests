# qyl-investigate eval

Replays the reviewer prompts of the qyl connector submission through Claude Code against
the live server and scores what came back: which tools were chosen, whether the answer
names the seeded findings, and what it cost. Run it before and after one change to the
qyl-investigate skill or to the server's instructions to see what the change did.

## What it needs

- `claude` (Claude Code CLI) signed in, and the qyl MCP server signed in as the reviewer
  under the server name `qyl` (`claude mcp list` shows `qyl: https://mcp.qyl.at/mcp - Connected`).
  The replay passes only that server (`--strict-mcp-config`), so the sign-in stored for the
  name `qyl` decides which project answers. The reviewer sign-in reaches `openai-review`, the
  project reseeded daily at 21:53 UTC with the answer-key incidents; the plugin's own server
  entry (`plugin:qyl:qyl`) is the owner's project and is not used.
- The installed qyl plugin (`claude plugin list` shows `qyl@ancplua-anthropic`), or a plugin
  directory passed with `--plugin-dir`. The replay loads the plugin with `--plugin-dir` and
  drops user-scope settings (`--setting-sources project`), so exactly one copy of the
  qyl-investigate skill is under test.
- Python 3.

## Run

```sh
python3 -I eng/evals/replay.py                       # the four read prompts, 3 runs each, sonnet
python3 -I eng/evals/replay.py --judge               # plus an LLM grade of each answer against its rubric (haiku)
python3 -I eng/evals/replay.py --case what-changed --runs 1 --model opus
python3 -I eng/evals/replay.py --tag writes          # the save-query case; it deletes what it saved
python3 -I eng/evals/replay.py --dry-run             # print the claude commands only
```

Results land in `results/<timestamp>-<label>/`: the raw stream and the parsed run per case
and run, `summary.json` and `summary.md`. The results directory is not committed.

## Measure one changed line

```sh
python3 -I eng/evals/replay.py --label baseline
python3 -I eng/evals/replay.py --variant evals/qyl-investigate/variants/example-one-line.json
python3 -I eng/evals/compare.py evals/qyl-investigate/results/<baseline dir> evals/qyl-investigate/results/<variant dir>
```

A variant spec names a file in the plugin and the exact line to replace; the replay copies
the plugin, applies the replacement (it must match once) and loads the copy. `compare.py`
prints B minus A per case: score, tool calls, turns, tokens, cost and seconds.

## Scores

| Score | Meaning |
| --- | --- |
| tools | share of the case's expected steps satisfied; each step lists the tools that satisfy it. 0 when a forbidden tool was called (the read prompts forbid every write tool) or an argument check failed (what-changed requires a 24 hour window). |
| mentions | share of the case's `must_mention` patterns found in the final answer: the findings, not ids or counts, because the project is reseeded with fresh ids every day. |
| judge | with `--judge`: a 0..1 grade of the final answer against the case's rubric by a second model. |
| score | mean of the components that ran. |

`verify_fix` may answer `pending` right after a reseed and `passed` once healthy traffic has
accumulated; the checkout case accepts the verdict as returned and the rubric asks for that.

## Binding the tool names

`tool-names.json` is the only place that knows the server's tool and argument names. Cases
use the logical names on its left side. After the coordinator's "tools final" (the T2 merge
renames and merges tools), update the right side and `bound_to`; the cases stay as they are.
