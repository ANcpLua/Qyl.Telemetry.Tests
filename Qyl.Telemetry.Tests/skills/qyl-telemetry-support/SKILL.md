---
name: qyl-telemetry-support
description: Use or extend the Qyl.Telemetry.Tests support library (OTLP receiver, typed evidence, assertions, telemetry policies, Collector and application fixtures, ChildProcess, scopes, waits, TimeProvider seams) and apply the binding test-code rules of the qyl family. Load before writing or reviewing any test that uses this library, here or in a consuming repository.
---

# Telemetry support library

Read [test-code-rules.md](references/test-code-rules.md) first; it binds every test and every support change. APIs and their sources: [api.md](references/api.md). Policies: [telemetry-policies.md](references/telemetry-policies.md).

## What lives where

- `Qyl.Telemetry.Tests` is a library (`IsTestApplication=false`). Executable cases live only in the three suites. A behavior the library needs is proven in `Qyl.Telemetry.UnitTests` (deterministic logic) or `Qyl.Telemetry.IntegrationTests` (transport, process, Collector).
- `Qyl.Telemetry.Tests.Capture` has no MSTest and no Collector dependency, so NativeAOT console fixtures can reference it. AutoInstrumentation references it through its `eng/dependencies/Qyl.Telemetry.Tests` submodule; an API change there needs that submodule bumped in the same wave.
- Upstream OTLP protos and their provenance: `Protos/upstream.json`.

## Extending the library

- A new capability is a real type with one job: a fixture (`CollectorFixture`, `InstrumentedApplicationFixture`), a scope (`EnvironmentScope`, `TelemetryPipeline.Ending`), a wait (`CollectorClient.WaitForTraceAsync`), a process wrapper (`ChildProcess`), a store seam (`IEvidenceStore`), a policy (`TelemetryPolicyAttribute` subclass with an exact violation and an accepted case). No shim, adapter or compatibility layer: replace the old code.
- Anything that reads the clock or waits takes a `TimeProvider` and is unit-tested with `FakeTimeProvider`.
- Every test a change touches is renamed and rewritten to the rules in the same commit; nothing is left half-converted.
- Public API of this library may change freely: it is a test library for agents and CI, not a published contract. Carry the change through the suites and the consumers' submodule pins.

## Gate

`dotnet build <project> -c Release` for every touched project, `dotnet test --project Qyl.Telemetry.UnitTests/Qyl.Telemetry.UnitTests.csproj -c Release`, `./build.sh --target InspectCode` to zero findings, then push; CI runs `./build.sh` (Fallout `Check`). Report executed, passed, failed and skipped counts, never "green" alone.
