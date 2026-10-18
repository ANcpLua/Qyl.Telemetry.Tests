# Port ownership and evidence

The receiver and reusable prototype support are absorbed into this repository.
The four projects have no project or package reference back to
`Qyl.Telemetry.Testing` or `mstest-architecture-prototype`. The original
repositories are retained as archived history. Existing published
`Qyl.Telemetry.Testing` packages remain available; external package consumers
have not automatically migrated to this source repository. Public Qyl production packages retain their own
production implementations and release ownership.

| Retained source responsibility | Destination |
| --- | --- |
| Canonical in-process OTLP receiver, typed records, raw requests and official generated protocol | Shared library root and `Protos/` |
| Trace graph, registry and protocol interpretation | Shared library; deterministic cases in UnitTests |
| Reusable architecture attributes, native execution composition, bounded trace observation and Collector fixture primitives | Shared `Architecture/` |
| Real OTLP transport, exporter interactions, payload presence/types and cancellation | IntegrationTests `Receiver/` |
| Collector process lifecycle and recorder restart/isolation | IntegrationTests `Collector/` |
| Orders/Payments workloads and application-specific scenario expectations | EndToEndTests `Applications/` and `Architecture/` |
| Native assembly lifecycle registration | Each consuming test assembly |

Receiver protocol generation preserves the eight official proto inputs pinned
in `Qyl.Telemetry.Tests/Protos/upstream.json`. Their hashes were checked against
the manifest during the port. Keep the accompanying license and
[third-party notices](../THIRD-PARTY-NOTICES.md).

Generic MSTest navigation probes, runner-contract child programs and reflection
emit tests were not copied into the telemetry suites. They verify the framework
extension/tooling contract rather than a Qyl telemetry interaction. The retained
telemetry cases still exercise native data rows, execution attributes, automatic
fixtures and invocation cancellation through their actual consumers.

The existing application baseline uses released packages, managed Collector
execution and REST investigation. It does not establish candidate-package
selection, Native AOT execution or MCP transport. Add those as explicit delivered
artifact scenarios, recording package/feed identity, executable RID/hash and
actual investigation interface. Collector retention and read-time redaction must
be verified against the corresponding Collector/MCP artifact, not inferred from
successful receiver capture.

## Production contract owners

Resolve the revision matching the selected packages before using these sources.

- [Qyl.Telemetry.SemanticConventions](https://github.com/ANcpLua/Qyl.Telemetry.SemanticConventions):
  registry vocabulary, typed signals and entities, generated definitions,
  deprecation mappings, persistence policy and analyzers.
- [Qyl.Telemetry.AutoInstrumentation](https://github.com/ANcpLua/Qyl.Telemetry.AutoInstrumentation):
  hosting and registration, native sources, interceptors/listeners, capture,
  signal export, package/generator compatibility and integration-specific NativeAOT behavior.
- [qyl](https://github.com/ANcpLua/qyl): Collector ingestion, storage, migrations,
  retention, metric fidelity, sessions, authorization, project isolation,
  investigation, streams, writable resources, orchestration and Qyl.Api.Sdk.
- [qyl-api-schema](https://github.com/ANcpLua/qyl-api-schema): public request,
  response, error, pagination and stream contracts, generated schemas and models,
  validators and contract revision.

Production-local generator, analyzer, storage-internal and schema-emitter cases
remain with their production owner. This solution owns reusable testing support
and the contracts connecting the selected products.
