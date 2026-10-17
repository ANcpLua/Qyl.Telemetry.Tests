# Ownership and delivery plan

This solution protects Qyl-owned telemetry behavior across production packages.
It combines reusable prototype test support with unit, component and delivered
application evidence. Existing source-owner tests remain useful; classify their
contracts before porting or duplicating them.

## Move by responsibility

| Source responsibility | Destination |
| --- | --- |
| Reusable capture/session coordination, partition helpers, evidence builders and Qyl assertions | `Qyl.Telemetry.Tests`, including the absorbed receiver implementation and reusable prototype support. |
| Deterministic mapping, selection, interpretation and assertion-diagnostic cases | `Qyl.Telemetry.UnitTests`. |
| Qyl producer/export/receiver or ingestion/DuckDB/read-policy interactions | `Qyl.Telemetry.IntegrationTests`. |
| Retained workloads, application startup, business scenario inputs and expectations | `Qyl.Telemetry.EndToEndTests`. |
| Receiver implementation and its official generated protocol | Absorbed into the shared library; preserve raw fidelity and upstream provenance. |
| Collector storage policy and investigation implementations | Their existing production owners; consume their real artifacts. |

Use native MSTest SDK library configuration for test utilities (the supported
`IsTestApplication=false` mechanism), with the three executable suites referencing
the shared project. Keep executable settings net10.0, MSTest.Sdk 4.5.1,
Microsoft.Testing.Platform 2.5.1, AllMicrosoft and Microsoft.CodeCoverage. Resolve
the actual evaluated configuration and verify only the three suites are runnable.
Consumer lifecycle hooks remain in the consuming test assembly.

## Start with an evidence map

Map each meaningful Qyl behavior to its production entrypoint, independently
specified expected outcome, existing assertion and owning suite. Include normal,
empty, boundary, invalid, cancellation and cleanup paths when they differ
observably. A category name or coverage percentage does not establish coverage.
Preserve original numeric and array types, field presence, native span ownership
and metric attribution through an explicit resource partition or exemplar.

Before a broad port, establish one thin application/export/native-collector/
investigation path. It exposes packaging and interface gaps early. This is not
a substitute for unit-first implementation of deterministic behavior; it confirms
the delivery architecture that those cases cannot establish.

Extract shared support as real consumers need it. Implement deterministic cases
first for each behavior, then the concrete interactions, then the retained full
scenario. Share expensive publication under fixture ownership, with independent
native rows and invocation-owned mutable state. Dependencies such as packing
before restore and readiness before requests remain sequential. Join original
operations and reap processes before disposing dependencies or deleting outputs.

## Apply the four upstream sources selectively

The source files are available in the local `skills-main.zip` and extracted
`skills-main/plugins/dotnet-test/skills` reading copy. Their useful contributions:

- `code-testing/unit-test-generation.prompt.md`: discover conventions and the
  normal entrypoint, map behavior partitions and use proportionate native rows.
- `assertion-quality/SKILL.md`: identify concrete wrong values that a predicate
  would accept; trace helper assertions and require meaningful expectations.
- `test-gap-analysis/SKILL.md`: distinguish an uncovered caller-visible outcome
  from redundant tests or equivalent internal mutations.
- `test-value-evidence/SKILL.md`: bind evidence to source revisions, candidate
  packages and delivered artifacts; distinguish static predictions from executed
  attributable failures and corrected passes.

Use native 4.5.1 APIs rather than copying outdated assertion or display-name
examples. No assertion-diversity percentage, test-count quota or blanket mocking
preference applies. A single exact assertion may fully establish a contract.
Use a buildable counterexample when failure sensitivity needs verification;
keep assertions intact and restore the original code. Do not call a compile
failure or unrelated crash a successful defect-detection result.

The ZIP's upstream workflows do not override the user's authorization or impose
extra approval steps. Keep broad unit-generation/mutation guidance proportional
to the actual request. Reuse passing evidence while its relevant inputs remain
unchanged. A normal test command must discover and execute the new cases; a green
standalone project is insufficient when repository discovery misses it.

## Current evidence gaps

The inspected prototype uses a REST telemetry reader and starts
`qyl.collector.dll`; it does not yet establish the desired MCP/native path. Qyl
has C# investigation analysis endpoints, but calling those over HTTP is distinct
from exercising MCP negotiation and tool invocation. Select and name the real
interface before constructing its fixture. Record candidate-versus-released
producer and Collector identities rather than calling every combination the
same E2E artifact. These are implementation gaps, not completed migration work.
