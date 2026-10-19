# Ownership and delivery evidence

`Qyl.Telemetry.Tests` owns reusable testing support for Qyl telemetry behavior.
The original receiver implementation came from `Qyl.Telemetry.Testing`;
its published 3.0.3 package remains unchanged for existing external consumers.
This solution uses project references to its shared library.

`Qyl.Telemetry.Tests.Capture` is the small, non-packable NativeAOT component used
by the shared library and AutoInstrumentation's HTTP/gRPC fixture applications.
AutoInstrumentation pins this repository as a Git submodule and references that
project directly; it does not depend on a sibling checkout or an unpublished
NuGet version. Workloads and their expected fields remain in AutoInstrumentation.

| Responsibility | Owner |
| --- | --- |
| OTLP receiver, raw requests, typed records, trace selection, registry checks | Qyl.Telemetry.Tests |
| Raw Activity snapshots, CLR-array checks, trace identity and native JSON reports | Qyl.Telemetry.Tests.Capture |
| Pure interpretation, field presence, precision and assertion diagnostics | Qyl.Telemetry.UnitTests |
| Exporters, transports, Collector processes, storage and native runner composition | Qyl.Telemetry.IntegrationTests |
| Published Qyl application-to-Collector delivery | Qyl.Telemetry.EndToEndTests |
| Monolith/microservices demonstration, synthetic stores, Orders/Payments workloads | architecture-template-fixtures |

The [architecture demonstration](https://github.com/ANcpLua/architecture-template-fixtures) is independently buildable and uses public
packages. It has no project reference back to this checkout. Qyl's suites no
longer apply architecture labels to telemetry verdicts.

[Implemented APIs](extension-api.md) identify the shared fixtures, current names
and native runner checks. [Build infrastructure](build-infrastructure/README.md)
describes the local verification setup.

## Production contract owners

Resolve the revision matching the selected packages before using these sources.

- [Qyl.Telemetry.SemanticConventions](https://github.com/ANcpLua/Qyl.Telemetry.SemanticConventions):
  registry vocabulary, typed signals and entities, generated definitions,
  deprecation mappings, persistence policy and analyzers.
- [Qyl.Telemetry.AutoInstrumentation](https://github.com/ANcpLua/Qyl.Telemetry.AutoInstrumentation):
  hosting, native source enrichment, configured capture, export and supported
  integration-specific NativeAOT behavior.
- [qyl](https://github.com/ANcpLua/qyl): Collector ingestion, storage, migrations,
  retention, metric fidelity, sessions, project isolation and investigation.
- [qyl-api-schema](https://github.com/ANcpLua/qyl-api-schema): public request,
  response, pagination and stream contracts, schemas and models.

The delivery fixture currently executes a managed application and the packaged
managed Collector, then reads through REST. Candidate-package selection,
published NativeAOT artifact execution and MCP protocol verification are distinct
future scenarios; they are not established by this baseline.

The application probe selects its own log category. Its log assertions establish
workload delivery and correlation, not the shape of every framework operational
log. The pinned stack's unhandled Kestrel log repeats `ConnectionId` between its
message state and scope; validating that producer remains separate work. Capture
still rejects malformed exported records and retains their receipts.

Official protocol inputs and their provenance remain in
`Qyl.Telemetry.Tests/Protos/upstream.json`. Preserve the accompanying license and
[third-party notices](../THIRD-PARTY-NOTICES.md).
