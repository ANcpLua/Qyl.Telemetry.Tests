---
name: qyl-telemetry-integration
description: Write tests in Qyl.Telemetry.IntegrationTests that export through real OTLP transports, AddQyl registration, the receiver, the relay and the packaged Collector, with scopes and waits from the support library instead of try/catch/finally.
---

# Integration tests

Binding rules: [test-code-rules.md](../../../Qyl.Telemetry.Tests/skills/qyl-telemetry-support/references/test-code-rules.md). Everything below is specific to this suite.

- Capture with `TelemetryCapture.TraceAsync` or `OtlpReceiver`; reach the packaged Collector through `CollectorFixture` or `TelemetryPipeline`; relay through `OtlpRelay`.
- Declare [telemetry policies](../../../Qyl.Telemetry.Tests/skills/qyl-telemetry-support/references/telemetry-policies.md) on a `[TracePerTest]` class; the policy is the assertion about the telemetry, the test asserts the behavior.
- Signals and metric shapes are the constants `Signals.Traces/Metrics/Logs` and `MetricShapes.*`; log value kinds are the class constants (`TextBody`, `BytesBody`). A `case` on a literal is a defect.
- State changes use scopes: `EnvironmentScope` with `[ResourceLock(WellKnownResources.EnvironmentVariables)]`, `TelemetryPipeline.Ending(root)`, `await using` for receivers and relays. A test that proves disposal declares the variable without `using` and disposes it explicitly, once.
- Waiting is `CollectorClient.WaitForTraceAsync` / `WaitForSpanEventsAsync` with a deadline; no polling loop in a test. Deadlines read the fixture's `TimeProvider`.
- Child runners go through `ChildProcess`. `Probes/InvocationContractTests` runs `Fixtures/TelemetryInvocation.Probe`; its `ProbeContract.cs` lists the probe case names and must follow every rename.
- Classes that change process-wide state (environment variables, the Collector) are `[DoNotParallelize]`; tests that wait carry `[Timeout]`.
- Run: `dotnet test --project Qyl.Telemetry.IntegrationTests/Qyl.Telemetry.IntegrationTests.csproj -c Release`; report the counts.
