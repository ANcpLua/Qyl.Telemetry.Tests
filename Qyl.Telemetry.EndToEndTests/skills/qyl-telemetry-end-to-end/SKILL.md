---
name: qyl-telemetry-end-to-end
description: Write tests in Qyl.Telemetry.EndToEndTests that drive an instrumented application into the packaged Collector and read it back through REST or the pinned MCP server, including the Analysis answer-key cases and the delivery of published packages.
---

# End-to-end tests

Binding rules: the root skill `/Users/alexandernachtmann/RiderProjects/Qyl.Telemetry.Tests/SKILL.md` (section "Test-code rules"). Everything below is specific to this suite.

- `Applications/TelemetryProbe` is the instrumented workload; `CaptureDeliveryFixture` starts it with a `TelemetryPipeline` and the packaged managed Collector.
- `Delivery/DeliveryTests` publishes managed and NativeAOT probes against the packages pinned in `Version.props`, builds a native Collector from the qyl commit in `Delivery/inputs.json` and calls the npm MCP server pinned there (`mcp.integrity`). Artifacts and logs are cached under `artifacts/delivery`; the first run needs network and a native compiler.
- `Analysis/` seeds independent incidents through OTLP into the packaged Collector and asks each analysis tool through its HTTP route and the pinned MCP server (`AnalysisPath`); `RestartTests` reopens the same data directory within one qyl version only, because the Collector's migrations are one-way. `AnswerKey` holds the seeded facts; a changed expectation is a finding about qyl or the seed, never a number edited to pass.
- Time is read through the session's `TimeProvider` (`TimeProvider.System` passed in once); seed timestamps are built from it with the named constants `Millisecond`, `Second` and `Minute`.
- Declare [telemetry policies](../../../Qyl.Telemetry.Tests/skills/qyl-telemetry-support/references/telemetry-policies.md); `[PreservedByCollector]` covers storage readback of spans.
- The four Analysis classes are the repin gate for a new qyl release: `dotnet test --project Qyl.Telemetry.EndToEndTests/Qyl.Telemetry.EndToEndTests.csproj -c Release --filter 'ClassName=Qyl.Telemetry.EndToEndTests.Analysis.AnswerKeyTests|ClassName=Qyl.Telemetry.EndToEndTests.Analysis.EngineDefectTests|ClassName=Qyl.Telemetry.EndToEndTests.Analysis.RestartTests|ClassName=Qyl.Telemetry.EndToEndTests.Analysis.ScanBudgetTests'`. Class names are therefore stable.
- Run all: `dotnet test --project Qyl.Telemetry.EndToEndTests/Qyl.Telemetry.EndToEndTests.csproj -c Release`; report the counts.
