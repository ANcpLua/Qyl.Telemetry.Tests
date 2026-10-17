# Qyl.Telemetry.Tests

This project is the shared support library referenced by UnitTests,
IntegrationTests and EndToEndTests. It owns Qyl capture orchestration, reusable
fixture adapters, evidence builders, resource partitions and domain assertions
that genuinely have more than one consumer. The project uses MSTest.Sdk with
`IsTestApplication=false`; it is a library, not a fourth test executable.

The receiver and evidence APIs from the old `Qyl.Telemetry.Testing` repository
are absorbed into this shared library. Maintain one OTLP server, official protocol
model and raw-value implementation here; the executable suites reference this
project. There is no package or runtime dependency back to either source repository.
Collector storage and investigation implementations retain their production owner. Move reusable prototype test
support here; application startup, workloads and scenario-specific expectations
stay beside the end-to-end scenarios.

Reusable support describes and observes behavior without deciding every
consumer's scenario. Keep immutable profile descriptions and owned shared
constants in one definition; policy enums stay typed. Give each invocation its
mutable state and evidence partition. Unit tests verify pure helper decisions;
integration tests verify concrete transport and lifecycle behavior. Application
scenarios belong to the end-to-end project rather than this support surface.

Fixture primitives can expose startup, endpoints, bounded observation, evidence
and disposal. Consumer lifecycle hooks remain in the consuming test assembly;
a library must not silently take assembly-wide ownership. Do not discover tests
or fixtures by assembly scanning when MSTest's supported composition, attributes
and MSBuild metadata supply the required contract.

An execution helper composes with the supplied original method attribute and
preserves its result array, data-row identities, cancellation and failures.
Multiple passive policy markers share one invocation. Required verification
failures remain visible; optional diagnostic failures have their own deliberate
policy. An evidence helper must preserve raw types and field presence and must
not turn missing evidence into success.

Use the absorbed receiver and public Qyl conventions; do not retain a second
receiver or a dependency on the old testing package. Extract a prototype helper only when
its consumers and stable contract justify reuse. An E2E-only publication detail
need not become part of this project's public API.
