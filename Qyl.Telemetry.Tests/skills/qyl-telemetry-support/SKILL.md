---
name: qyl-telemetry-support
description: Use or extend the Qyl.Telemetry.Tests support library for OTLP capture, typed evidence, assertions, telemetry policies and Collector or application fixtures.
---

# Telemetry support library

- APIs and their sources: [api.md](references/api.md). Policies: [telemetry-policies.md](references/telemetry-policies.md).
- Qyl.Telemetry.Tests is a library (`IsTestApplication=false`); executable cases live in the three suites.
- Qyl.Telemetry.Tests.Capture stays free of MSTest and Collector dependencies so NativeAOT console fixtures can use it. AutoInstrumentation references it through its `eng/dependencies/Qyl.Telemetry.Tests` submodule, so an API change there needs that submodule bumped.
- Upstream OTLP protos and their provenance: `Protos/upstream.json`.
- Prove deterministic support logic in UnitTests and transport or process behavior in IntegrationTests.
