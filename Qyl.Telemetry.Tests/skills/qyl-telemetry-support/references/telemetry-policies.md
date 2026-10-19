# Telemetry policies

A telemetry policy is a passive attribute on a test method or class. After a
passing test, `[TracePerTest]` verifies each declared policy against the traces
the test published. A violation turns the result into a failure listing every
violation with its span, a span-tree outline and an attached
`telemetry-evidence.txt`. A failed, inconclusive or ignored test keeps its own
outcome.

## Declaring

1. Put `[TracePerTest]` on the class in place of `[TestClass]` and import
   `Qyl.Telemetry.Tests.Policies`; a plain test class that declares a policy
   fails as soon as it publishes evidence.
2. Publish evidence inside the test: `TelemetryCapture.TraceAsync` publishes its
   trace, and `TelemetryPipeline` publishes every root it began when it stops.
   A declared policy without a published trace fails.
3. Declare the obligations the scenario promises. Class declarations apply to
   every test, including inherited ones; a method declaration replaces the class
   declaration of the same policy.
4. Keep scenario values in the test: expected header values, metric points and
   specific parentage (`Assert.HasSingleNativeSpan`, `Assert.HasParentContext`,
   `Assert.That.HasCapturedValues`).

## Policies

| Policy | Proves | Scope |
| --- | --- | --- |
| `[ConnectedTrace]` | Every span's parent is a captured span of its trace or the test root; no second root. | Test traces |
| `[NoDuplicateSpans]` | No span identity is exported twice; no client, server, producer or consumer span wraps its own operation; no second scope mirrors a concurrent operation. | Test traces |
| `[CapturesExactly(fields)]` | The captured HTTP header and RPC metadata attributes equal `fields`; none lost, none added. No arguments: nothing captured. | Test traces |
| `[ConformsToSemanticConventions]` | Registered attributes have their value types, registered metrics their units, and nothing deprecated appears unless `AllowDeprecated`. | Whole captures |
| `[PreservedByCollector]` | The Collector stores each captured span with its name, parent, kind, status, times, scope, events, and span and resource attributes that `RegistryPolicy.IsPersisted` keeps, with typed values. Denied keys stay out; only copies of the span's resource attributes may be added. Exception text compares exactly. | Test traces, `TelemetryPipeline` only |
| `[NoErrorTelemetry]` | No test span has error status or an exception event, and no captured log reaches Error severity. | Test traces; logs across whole captures |
| `[ExportsCleanly]` | Every export was fully accepted: no rejected record, export error or receiver warning. | Whole captures |

Test traces are the published traces; whole captures also contain fixture
traffic such as health probes and runtime metrics.

## Built-in guarantees

- A Collector that became ready must exit with code 0 within 10 seconds of
  SIGTERM; `CollectorFixture` disposal fails otherwise (not on Windows).
- `TelemetryPipeline` fails on rejected records and incomplete deliveries for
  every test, with or without policies.

## Adding a policy

Add one sealed `*Attribute` in `Qyl.Telemetry.Tests/Policies/` deriving from
`TelemetryPolicyAttribute`. Implement `Violations` as complete sentences that
name the span through `Describe` and state the observed value. Prove it in
`Qyl.Telemetry.UnitTests/Policies` with an exact violation and an accepted
case, and in `PolicyCases` when it changes runner behavior. Add a policy when
its obligation holds for any scenario that declares it; a check that needs a
scenario's expected values stays an assertion in that scenario.

## Rejected proposals

These obligations need scenario-specific expectations or belong to another
owner; propose them again only with a scenario-independent rule.

- Owned by qyl's own tests: exception sanitization, project isolation,
  durability across restart, API contract shapes, query pagination,
  investigation coverage, optimistic concurrency.
- Already enforced: `RequiresTelemetryEvidence` (every policy fails without
  evidence), `HonorsOtlpRejectionContract` (`ExportsCleanly`, `TelemetryPipeline`),
  `CompletesGracefulShutdown` (`CollectorFixture`), `UsesCandidatePackages` and
  `RunsPublishedNativeArtifact` (`DeliveryArtifacts` verifies restored packages
  and executable hashes).
- Covered by assertions: `PreservesMetricSemantics` (units through
  `ConformsToSemanticConventions`; point values are scenario expectations).
- Covered by native data: `OtlpTransportData` and `CapturedFieldData`
  (`[DataRow(OtlpTransport.Grpc)]` and scenario constants).
