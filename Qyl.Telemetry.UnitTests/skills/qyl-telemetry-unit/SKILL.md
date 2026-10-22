---
name: qyl-telemetry-unit
description: Write deterministic tests in Qyl.Telemetry.UnitTests for decoding, registry validation, trace evidence, assertions, telemetry policies and the support library's timed and stored logic, one behavior per test, with faked dependencies and FakeTimeProvider.
---

# Unit tests

Binding rules: [test-code-rules.md](../../../Qyl.Telemetry.Tests/skills/qyl-telemetry-support/references/test-code-rules.md). Everything below is specific to this suite.

- Build records in memory (`SpanRecord`, `TelemetrySnapshot`, `TraceEvidence`, `ActivitySnapshot`). Start no receiver, process or network connection; touch no file system (policy evidence goes through an in-memory `IEvidenceStore`); read no clock (`FakeTimeProvider`).
- One behavior per test, named `Subject_State_Expected`. A fixture or helper builds data and runs the operation; the test holds the assertion. Many small tests are the goal.
- Each telemetry policy has an exact violation test and an accepted test in `Policies/`; the assertion names the exact diagnostic text or field, not "a failure".
- Data rows select behavior through `private const string` discriminators and enum members (`RawViolation`); every enum member is handled in the switch the helper uses, with `UnreachableException` for the branch the caller excludes.
- Timed support logic (`CollectorClient` waits, deadlines) is tested by advancing `FakeTimeProvider`, never by sleeping.
- Run: `dotnet test --project Qyl.Telemetry.UnitTests/Qyl.Telemetry.UnitTests.csproj -c Release`; report the counts.
