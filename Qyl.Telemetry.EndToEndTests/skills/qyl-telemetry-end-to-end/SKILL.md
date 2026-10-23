---
name: qyl-telemetry-end-to-end
description: Write tests in Qyl.Telemetry.EndToEndTests that drive an instrumented application into the locally staged Collector and read it back through REST or the matching source MCP server, including the Analysis answer-key cases and the separate delivery of published packages.
---

# End-to-end tests

Binding rules: the [root skill](../../../SKILL.md) (sections "MSTest 4.5.1 and MTP: verified usage" and "Test-code rules"). Everything below is specific to this suite.

- `Applications/TelemetryProbe` is the instrumented workload; `CaptureDeliveryFixture` starts it with a `TelemetryPipeline` and the locally staged managed Collector.
- `Applications/SemanticContractProbe` is the separate executable for the published semantic-contract checks. Its ordinary build can use the local SemanticConventions checkout; Delivery explicitly sets `UseLocalQylSemanticConventions=false` to verify the pinned packages. Both applications are build-only project references (`ReferenceOutputAssembly=false`); keep their source excluded from this test assembly. Delivery publishes them explicitly for its target runtime.
- `Delivery/DeliveryTests` publishes managed and NativeAOT probes against the packages pinned in `Version.props`, builds a native Collector from the qyl commit in `Delivery/inputs.json` and calls the npm MCP server pinned there (`mcp.integrity`). Artifacts and logs are cached under `artifacts/delivery`; the first run needs network and a native compiler.
- `Analysis/` seeds independent incidents through OTLP into the locally staged Collector and asks each analysis tool through its HTTP route and the matching MCP source entry point under Bun (`AnalysisPath`). The build records that entry point in `local-mcp-entrypoint.txt`; both processes must use the same contract revision. `RestartTests` reopens the same data directory within one qyl version only, because the Collector's migrations are one-way. `AnswerKey` holds the seeded facts; a changed expectation is a finding about qyl or the seed, never a number edited to pass.
- A class that shares one Collector across its tests holds it in a `SharedAnalysisSession` (`RestartTests` starts one per test). Seeds wait for their spans through `OtlpIncidents.SendAndWaitAsync`; analysis inputs come from `AnswerKey.Window` and `AnswerKey.Matcher`.
- Time is read through the session's `TimeProvider` (`TimeProvider.System` passed in once); seed timestamps are built from it with the named constants `Millisecond`, `Second` and `Minute`.
- Declare [telemetry policies](../../../Qyl.Telemetry.Tests/skills/qyl-telemetry-support/references/telemetry-policies.md); `[PreservedByCollector]` covers storage readback of spans.
- The four Analysis classes (`AnswerKeyTests`, `EngineDefectTests`, `RestartTests`, and `ScanBudgetTests`) are the repin gate for a new qyl release. Keep their class names stable and run them through the repository's EndToEnd suite entry point below.
- Run all: `pwsh ./eng/test.ps1 -Suite EndToEnd`; report the counts.
