---
name: qyl-telemetry-integration
description: Write tests in Qyl.Telemetry.IntegrationTests that export through real OTLP transports, AddQyl registration, the receiver and the packaged Collector.
---

# Integration tests

- Capture with `TelemetryCapture.TraceAsync` or `OtlpReceiver`; reach the packaged Collector through `CollectorFixture` or `TelemetryPipeline`.
- Declare [telemetry policies](../../../Qyl.Telemetry.Tests/skills/qyl-telemetry-support/references/telemetry-policies.md) on a `[TracePerTest]` class.
- Classes that change process-wide state (environment variables, the Collector) are `[DoNotParallelize]`.
- `Probes/InvocationContractTests` runs the `Fixtures/TelemetryInvocation.Probe` child runner; extend it when runner behavior changes.
- Run: `dotnet test --project Qyl.Telemetry.IntegrationTests/Qyl.Telemetry.IntegrationTests.csproj -c Release`.
