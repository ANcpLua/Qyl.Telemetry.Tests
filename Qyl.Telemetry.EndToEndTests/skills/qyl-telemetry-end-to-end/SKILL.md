---
name: qyl-telemetry-end-to-end
description: Write tests in Qyl.Telemetry.EndToEndTests that drive an instrumented application into the Collector and read it back through REST or MCP.
---

# End-to-end tests

- `Applications/TelemetryProbe` is the instrumented workload; `CaptureDeliveryFixture` starts it with a `TelemetryPipeline` and the packaged managed Collector.
- `Delivery/DeliveryTests` publishes managed and NativeAOT probes against the packages pinned in `Version.props`, builds a native Collector from the qyl commit in `Delivery/inputs.json` and calls the npm MCP server pinned there. Artifacts and logs are cached under `artifacts/delivery`.
- `Analysis/` seeds independent incidents through OTLP into the packaged Collector and asks each analysis tool through its HTTP route and the pinned MCP server (`AnalysisPath`); `RestartTests` reopens the same data directory.
- Declare [telemetry policies](../../../Qyl.Telemetry.Tests/skills/qyl-telemetry-support/references/telemetry-policies.md); `[PreservedByCollector]` covers storage readback of spans.
- Run: `dotnet test --project Qyl.Telemetry.EndToEndTests/Qyl.Telemetry.EndToEndTests.csproj -c Release`.
