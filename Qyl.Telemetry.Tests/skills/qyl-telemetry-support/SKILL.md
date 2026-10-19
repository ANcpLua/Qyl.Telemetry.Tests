---
name: qyl-telemetry-support
description: Build or review reusable support in Qyl.Telemetry.Tests, including OTLP capture, typed evidence, assertions, telemetry policies and native MSTest lifecycle composition.
---

# Reusable telemetry support

Resolve package APIs or delivery assumptions through the
[execution context](references/execution-context.md) when relevant.
Target Qyl.Telemetry.Tests/Qyl.Telemetry.Tests.csproj. Raw Activity evidence shared
with NativeAOT applications belongs in Qyl.Telemetry.Tests.Capture, which has no
MSTest or Collector dependency and is referenced by the full support library.

Own the canonical receiver, raw and typed captures, export receipts, graphs,
resource and scope identities, conformance decisions, assertions and lifecycle
primitives. Preserve upstream protocol provenance through Protos/upstream.json.
Keep application workloads and scenario-specific expected values in consumers.

Inspect existing support and resolved native MSTest APIs before extending them.
Extract a shared abstraction when consumers establish its responsibility.
Keep the project a support library through its evaluated IsTestApplication
setting; executable cases belong in the consuming suites.

Read the [implemented API map](../../../docs/extension-api.md) when selecting
TracePerTestAttribute, TelemetryCapture and the isolated Collector/application
fixtures. Extend the applicable existing composition point.
Delegate to the original method attribute once and preserve every native result,
data case, cancellation and failure. Keep policy declarations passive. Let
MSTest own discovery and row expansion.

Bound observation and teardown. Isolate mutable evidence, join outstanding work
before disposal and retain both original failures and required cleanup failures.
Keep optional diagnostics distinct from required evidence.

Place deterministic support decisions in UnitTests and real transport or
process behavior in IntegrationTests.

When declaring, adding or reviewing a telemetry policy, read
[Telemetry policies](references/telemetry-policies.md).
