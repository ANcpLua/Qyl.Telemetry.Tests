# Qyl public stack and signal boundaries

Resolve actual producer, convention, receiver and Collector versions before
implementation. Central package pins and restored assets identify the current
stack; the receiver and prototype support now live in this shared library.

## Select the producer and observation boundary

For an application using `Qyl.Telemetry.AutoInstrumentation`, use its supported
public host setup. In the inspected Hosting 28.2.0 package, that is
`builder.AddQyl(options => ...)` in namespace `Qyl`, supplied by
`Qyl.Telemetry.Hosting`. Recheck the resolved package before implementing.
Configure `CollectorEndpoint`, `ServiceName` and any additional application
sources through the supported options. Reuse published
`Qyl.Telemetry.SemanticConventions` constants for known keys and activity sources;
use named constants for owned repeated strings and typed enums for selectors.

A consuming test should exercise that setup instead of building a second
instrumentation pipeline with SDK `AddInstrumentation`, processors or duplicate
spans. Qyl's own implementation may legitimately use provider-owned
`AddInstrumentation` internally. A test specifically of an SDK exporter may
also use that SDK directly; it establishes a different contract. Neither use
implies CLR profiling or Native AOT compatibility.

Observe concrete OTLP through this repository's `Qyl.Telemetry.Tests` shared library
where that is the intended boundary. Its receiver preserves raw payloads, typed values and field presence;
it does not own Collector persistence policy. Collector integration tests use
the actual Collector payload/conversion/storage types. Read-surface privacy is
a separate boundary reached through the actual redactor or read API.

## Compose fixture ownership with native cases

Consumer lifecycle hooks live in the consuming test assembly. Keep shared
startup/publication under its fixture's cancellation and ownership; give each
native data row its own observation partition and mutable scenario state.
Forward the invocation's `TestContext.CancellationToken` to cancellable waits.
A single test's cancellation must not dispose a shared publication another row
uses. Async dependency sequences remain sequential; run only independent work
concurrently. Stop and join children before deleting their workspaces.

A receiver fixture supplies endpoint selection, bounded observation, isolation
and disposal. The test supplies independent expected values and required
outcomes. Specify the OTLP protocol explicitly rather than inferring it from a
port. Complete the producer operation and required flush before concluding that
an expected signal is missing. Metric attribution needs a resource partition or
exemplar; trace identity alone does not partition an unrelated aggregate metric.

A telemetry-backed attribute composes with the original MSTest executor once,
preserves its results and distinguishes policy rejection from insufficient
evidence. Its markers select typed policies; the fixture observes rather than
manufactures evidence. Native span ownership requires identities and
relationships, not only a count.

This file describes Qyl-specific package and signal boundaries. The generic
MSTest plugin supplies native API/composition guidance independently; no
repository path or Qyl dependency belongs in that shared plugin.
