# Implemented extension APIs

The APIs below exist in C# source in this repository. The
[policy catalogue](../Qyl.Telemetry.Tests/skills/qyl-telemetry-support/references/telemetry-policies.md)
contains separate proposals, not implemented attributes.

## Receiver invocation scope

[`QylTest.cs`](../Qyl.Telemetry.Tests/QylTest.cs) contains:

- `QylTest.TraceAsync`: captures an operation with an owned receiver and trace
  root, preserving raw and typed evidence.
- `QylTelemetryAttribute`: a `TestClassAttribute` that wraps the method attribute
  supplied by MSTest. Its internal `QylTelemetryMethodAttribute` establishes a
  `qyl.test` activity and awaits the original executor once.
- `QylTestHooks`: consuming-assembly lifecycle helpers that expose and clear the
  trace identity through `TestContext.Properties`.

These were ported from the old `Qyl.Telemetry.Testing` namespace into
`Qyl.Telemetry.Tests`; the receiver wrapper was not removed or replaced.
[`IsolationTests`](../Qyl.Telemetry.IntegrationTests/Receiver/IsolationTests.cs)
uses `[QylTelemetry]` and verifies the invocation trace identity. The consuming
[lifecycle declarations](../Qyl.Telemetry.IntegrationTests/Lifecycle.cs) register
the hooks through native MSTest attributes.

## Architecture composition

[`ArchitectureAttribute`](../Qyl.Telemetry.Tests/Architecture/ArchitectureAttribute.cs)
is another `TestClassAttribute`. Its `Monolith`, `Microservices` and
`DistributedMonolith` profiles wrap the original executor through
[`ArchitectureTestMethodAttribute`](../Qyl.Telemetry.Tests/Architecture/ArchitectureTestMethodAttribute.cs).
They acquire a fixture-owned running system, observe the invocation's telemetry
and evaluate the exercised service/store relationships.

[`ArchitectureFixtures`](../Qyl.Telemetry.Tests/Architecture/ArchitectureFixtures.cs)
provides lazy assembly ownership and teardown; consumer assemblies declare its
MSTest lifecycle hooks. Native `TestContext.Current` is used within the executor
under its experimental API annotation.

[`Architecture.QylTelemetry`](../Qyl.Telemetry.Tests/Architecture/QylTelemetry.cs)
is an HTTP investigation reader. It is a different type from the receiver's
`QylTelemetryAttribute`.

Both execution wrappers use MSTest's public
`GetTestMethodAttribute(TestMethodAttribute)` and
`ExecuteAsync(ITestMethod)` composition hooks. They do not invoke a test method
through reflection. The architecture executor does read class attribute
metadata; that alone does not establish generated-discovery or NativeAOT test
compatibility.

## Runner probes still to review

The archived `tests/Qyl.Telemetry.Testing.Tests/NativeRunnerTests.cs` and
`tests/Qyl.Telemetry.Testing.Probes/ProbeCases.cs` checked native data-row identity,
original assertion/exception results and source locations for `[QylTelemetry]`.
Those contracts remain relevant because the wrapper still exists. The new
solution has not retained the two child-runner tests.

The old harness assumes `bin/<configuration>/net10.0`, finds its source tree by
walking parent directories, and loads the child assembly through reflection to
obtain expected source metadata. Reuse its observable assertions only after
reviewing fixture deployment through MSBuild and expectations derived without
loading the child assembly. A passing receiver isolation test does not replace
explicit failure-result or source-navigation evidence.
