# Qyl.Telemetry.IntegrationTests

Verify Qyl-owned interactions among concrete components: receiver/exporter transport,
capture configuration, typed OTLP conversion, correlation, isolation, flush and
disposal. Run the actual component boundary being claimed. Replacing the
exporter, receiver or conversion path with a mock would remove that evidence.
Pure selection/expectation logic can remain a unit test.

A receiver fixture supplies protocol-correct endpoints, bounded observation and
shutdown. The consuming assembly owns lifecycle registration; a reusable library
supplies primitives rather than silently installing hooks. Rows own mutable
capture state and correlation partitions. Shared startup uses its fixture's
cancellation; an invocation can cancel its wait without canceling every other
row's resource. Stop and join work before disposing dependencies.

Exercise application instrumentation through Qyl's public registration when the
producer is in scope. A transport-only test can submit real DTOs through the
concrete transport and must label that narrower claim. Assert actual typed
values, field presence and relevant source/parent relationships. Complete the
operation and required flush before treating absence as final.

Select cases from the changed contract: empty/unset opt-in configuration,
normalization collisions on one record, single/multi-value arrays, general and
content headers, no content/response, duplicate metadata, binary entries and
initial/trailing response phases. Use actual incompatible binary entries when
accessor safety is the contract. Independent profiles are native rows, not a
manual loop whose first failure prevents later cases from running.

Persistence is a Collector contract only when the real ingestion/storage path
is included. Ingestion rejection and read-time redaction are separate assertions;
required metric changes need an isolated measurement, not field absence alone.
See [Qyl stack](qyl-stack.md) for package and signal ownership. A green in-process
component run is not evidence for candidate-package startup or Native AOT.

`AddQyl()` plus export into the canonical receiver proves the producer/capture
contract. Ingestion/conversion into DuckDB proves storage only when the actual
Qyl ingestion and storage components run. Read-time redaction uses the real
investigation/read entrypoint. Choose the interaction the test promises; starting
the entire stack for every component assertion adds no automatic confidence.
Do not add integration tests merely to re-prove upstream OTLP, HTTP or gRPC
behavior without an identifiable Qyl contribution.
