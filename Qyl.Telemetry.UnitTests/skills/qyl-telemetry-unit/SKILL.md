---
name: qyl-telemetry-unit
description: Write deterministic tests in Qyl.Telemetry.UnitTests for decoding, registry validation, trace evidence, assertions and telemetry policies.
---

# Unit tests

- Build records in memory (`SpanRecord`, `TelemetrySnapshot`, `TraceEvidence`); start no receiver, process or network connection.
- Assert exact diagnostics: each telemetry policy has an exact violation and an accepted case in `Policies/`.
- Run: `dotnet test --project Qyl.Telemetry.UnitTests/Qyl.Telemetry.UnitTests.csproj -c Release`.
