---
name: qyl-telemetry-integration
description: Write or review selected real component interactions in Qyl.Telemetry.IntegrationTests, including AddQyl registration, OTLP transport, capture, Collector storage and lifecycle.
---

# Real component boundaries

Resolve package APIs or delivery assumptions through the
[execution context](../../../Qyl.Telemetry.Tests/skills/qyl-telemetry-support/references/execution-context.md) when relevant.
Target Qyl.Telemetry.IntegrationTests/Qyl.Telemetry.IntegrationTests.csproj.

Name the boundary and required evidence: producer output, wire representation,
stored representation or public response. Run the real components at that
boundary and isolate unrelated dependencies.

Exercise public AddQyl registration when configuration or instrumentation is
the subject. Use a direct SDK exporter only for its explicitly selected transport
contract. Select the OTLP protocol explicitly and reuse the canonical receiver.

Choose applicable obligations for native ownership, HTTP headers, gRPC metadata,
typed OTLP values, rejection, limits, normalization, persistence, restart or
isolation. Resolve capture, persistence and exception rules for the selected
artifact. Require actual readback for storage claims.

Give each invocation an evidence partition and owned mutable state. Keep shared
startup cancellation separate from a row's wait. Bound readiness, observation,
export completion and shutdown; join work before disposing dependencies.

Complete the producer operation and required flush before asserting absence.
Keep configuration, identities, receipts and observations in relevant failure
diagnostics. Use the affected boundary cases for executable changes.

Declare the [telemetry policies](../../../Qyl.Telemetry.Tests/skills/qyl-telemetry-support/references/telemetry-policies.md)
the scenario promises on a `[TracePerTest]` class. Move repeated
interpretation or execution into [shared support](../../../Qyl.Telemetry.Tests/skills/qyl-telemetry-support/SKILL.md).
