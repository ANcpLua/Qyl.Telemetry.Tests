---
name: qyl-telemetry-tests
description: Write or review telemetry tests in Qyl.Telemetry.Tests. Route work to this repository's unit, integration, end-to-end or shared-support boundary before choosing Qyl packages, fixtures, instrumentation and evidence.
---

# Qyl test project boundaries

This is repository-local guidance for
[Qyl.Telemetry.Tests.slnx](../../../Qyl.Telemetry.Tests.slnx). Select the requested
behavior and project before writing tests. The project boundaries have different
inputs, resource owners and evidence; they do not become interchangeable because
they all use MSTest. Their combined scope is Qyl-owned
instrumentation, conventions, capture, collection, storage and investigation.

| Project | Intended boundary | Read when relevant |
| --- | --- | --- |
| `Qyl.Telemetry.UnitTests` | Deterministic Qyl mapping, registry validation, trace selection, resource partitioning, numeric conversion, evidence interpretation and assertion diagnostics. | [Unit tests](references/unit.md) |
| `Qyl.Telemetry.IntegrationTests` | Concrete Qyl-owned interactions: AddQyl/export/receiver, ingestion/conversion/DuckDB, isolation, redaction, flushing, cancellation and cleanup. | [Integration tests](references/integration.md) |
| `Qyl.Telemetry.EndToEndTests` | Retained applications using candidate Qyl packages, a published Native AOT Qyl executable and the intended C# MCP investigation path. | [End-to-end tests](references/end-to-end.md) |
| `Qyl.Telemetry.Tests` | Shared support library referenced by all three executable suites: capture orchestration, receiver fixture adapters, evidence builders, partitions and Qyl assertions. | [Shared support](references/shared-support.md) |

For migration work, first read the [ownership and delivery plan](references/migration.md).
Read only the matching scope reference for focused work. For real producer/receiver work, also use
[Qyl stack and signal boundaries](references/qyl-stack.md). A pure unit test does
not need that fixture or a running receiver.

Current setup (2026-10-17): `global.json` pins .NET SDK 10.0.401 and
MSTest.Sdk 4.5.1 and selects Microsoft.Testing.Platform. Restored MTP is 2.5.1.
All projects target net10.0 with AllMicrosoft; the shared project sets
`IsTestApplication=false` and evaluates to a library. Only the three suites run.
Unit and integration cases use method-level parallelism, with explicit isolation
for process-wide Collector cases. Application scenarios run sequentially.
Re-read actual configuration and restored assets after dependency changes.

Use native data rows for independent selectable cases, preserve original
execution results and flow native invocation cancellation. Share immutable
publication only when its fixture owns startup and shutdown; mutable evidence
and configuration belong to an isolated invocation. Keep typed enum selectors
and shared string constants in their distinct roles.

The unit-test agent article informs repository research, scenario mapping,
assertion strength and normal-runner discovery. Its unit-only isolation advice
belongs to the unit project. The other projects deliberately exercise real
boundaries. Verify the changed cases through the repository's actual entrypoint;
report discovery and executed evidence separately. Avoid adding a separate build
or full-suite run when it establishes no additional contract.
