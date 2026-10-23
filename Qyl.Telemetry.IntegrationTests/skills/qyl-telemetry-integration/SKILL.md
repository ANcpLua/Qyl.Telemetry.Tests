---
name: qyl-telemetry-integration
description: Write tests in Qyl.Telemetry.IntegrationTests that export through real OTLP transports, AddQyl registration, the receiver, the relay and the parent checkout's Collector staged by the build, with scopes and waits from the support library instead of try/catch/finally.
---

# Integration tests

Binding rules: the [root skill](../../../SKILL.md) (sections "MSTest 4.5.1 and MTP: verified usage" and "Test-code rules"). Everything below is specific to this suite.

- Capture with `TelemetryCapture.TraceAsync` or `OtlpReceiver`; reach the parent checkout's Collector staged by the build through `CollectorFixture` or `TelemetryPipeline`; relay through `OtlpRelay`.
- Declare [telemetry policies](../../../Qyl.Telemetry.Tests/skills/qyl-telemetry-support/references/telemetry-policies.md) on a `[TracePerTest]` class; the policy is the assertion about the telemetry, the test asserts the behavior.
- Raw OTLP requests go through `OtlpHttp` (`PostAsync`, `Content`, `Compress`) and `OtlpGrpc.ExportAsync`, export envelopes through `OtlpEvidence.Traces/Logs/Metrics`, reference metrics through `MetricFixture.Of`. A test class never calls into another test class.
- Signals and metric shapes are the constants `Signals.Traces/Metrics/Logs` and `MetricShapes.*`; log value kinds are the class constants (`TextBody`, `BytesBody`). A `case` on a literal is a defect.
- State changes use scopes: `EnvironmentScope` with `[ResourceLock(WellKnownResources.EnvironmentVariables)]`, `TelemetryPipeline.Ending(root)`, `await using` for receivers and relays. A test that proves disposal declares the variable without `using` and disposes it explicitly, once.
- Waiting is `CollectorClient.WaitForTraceAsync` / `WaitForSpansAsync` / `WaitForSpanEventsAsync` with a deadline; no polling loop in a test. Deadlines read the fixture's `TimeProvider`.
- Child runners go through `ChildProcess`. `Probes/InvocationContractTests` runs `Fixtures/TelemetryInvocation.Probe`; its `ProbeContract.cs` lists the probe case names and must follow every rename.
- Classes that change process-wide state (environment variables, the Collector) are `[DoNotParallelize]`; tests that wait carry `[Timeout]`.
- Run: `pwsh ./eng/test.ps1 -Suite Integration`; report the counts.

- `Contracts/` contains the shared C#/TypeScript contract fixture cases and the real Collector consumer cases. Keep their assertions, data rows and fixture files in this integration project. The parent SDK versions come from root `global.json`.
