# Telemetry testing APIs

The supported APIs are implemented in this repository. Policy ideas in the
[optional catalogue](../Qyl.Telemetry.Tests/skills/qyl-telemetry-support/references/telemetry-policies.md)
remain proposals unless linked to an implementation.

| Capability | Implementation |
| --- | --- |
| Independent native MSTest trace for each invocation and data row | [TracePerTestAttribute](../Qyl.Telemetry.Tests/TelemetryCapture.cs) |
| Capture one operation with an owned OTLP receiver | [TelemetryCapture.TraceAsync and CaptureContext](../Qyl.Telemetry.Tests/TelemetryCapture.cs) |
| Typed evidence, trace selection, parentage, calls and store identities | [TraceEvidence](../Qyl.Telemetry.Tests/TraceEvidence.cs) |
| Exact captured arrays, including duplicates and empty strings | [Assert.That.HasCapturedValues](../Qyl.Telemetry.Tests/CapturedAttributeAssertions.cs) |
| One native span, preserved parent context and unique exported identities | [SpanAssertions](../Qyl.Telemetry.Tests/SpanAssertions.cs) |
| Raw CLR array, parent/trace identity and disposal checks in native applications | [CapturedActivityAssertions](../Qyl.Telemetry.Tests.Capture/CapturedActivityAssertions.cs) |
| Detached Activity snapshots and source-filtered export | [ActivitySnapshot](../Qyl.Telemetry.Tests.Capture/ActivitySnapshot.cs), [ActivitySnapshotExporter](../Qyl.Telemetry.Tests.Capture/ActivitySnapshotExporter.cs) |
| Versioned HTTP/gRPC fixture JSON | [ActivityCaptureReport](../Qyl.Telemetry.Tests.Capture/ActivityCaptureReport.cs) |
| Protocol and registry conformance | [OtlpReceiver](../Qyl.Telemetry.Tests/OtlpReceiver.cs), [RegistryConformance](../Qyl.Telemetry.Tests/RegistryConformance.cs) |
| Isolated published Collector process and cleanup | [CollectorFixture](../Qyl.Telemetry.Tests/Collector/CollectorFixture.cs) |
| Managed or native executable identity and integrity | [PublishedExecutable](../Qyl.Telemetry.Tests/Collector/PublishedExecutable.cs) |
| Collector ingestion and investigation | [CollectorClient](../Qyl.Telemetry.Tests/Collector/CollectorClient.cs) |
| Capture, forwarding, persistence and diagnostic files | [TelemetryPipeline](../Qyl.Telemetry.Tests/Collector/TelemetryPipeline.cs) |
| Instrumented child application and complete delivery fixture | [InstrumentedApplicationFixture](../Qyl.Telemetry.Tests/Collector/InstrumentedApplicationFixture.cs) |

`TracePerTestAttribute` composes with the original method attribute through
MSTest's `GetTestMethodAttribute(TestMethodAttribute)` hook. It creates one
native `Activity` around the invocation and awaits the original executor once.
Data expansion, failures, inconclusive outcomes and discovery stay with MSTest.
No global initialization hook or copied TestContext property is needed.
`TelemetryPipeline` borrows the invocation TestContext for its workspace and
automatically attaches retained evidence during disposal. Consumer classes keep
their native initialization and cleanup hooks.

[InvocationContractTests](../Qyl.Telemetry.IntegrationTests/Probes/InvocationContractTests.cs)
executes the real child runner and checks row identities, original executors,
assertion/exception results and source navigation. MSBuild deploys the child
payload through `GetTargetPath`; the parent does not load the child assembly to
inspect its metadata. These are managed runner checks, not a NativeAOT claim.

## Breaking name changes

| Previous name | Current name |
| --- | --- |
| QylTelemetryAttribute | TracePerTestAttribute |
| QylTest | TelemetryCapture |
| QylCapture | CaptureContext |
| QylTrace | TraceEvidence |
| QylCollector | CollectorFixture |
| Architecture.QylTelemetry | CollectorClient |
| SpanRecorder | TelemetryPipeline |
| CaptureAssert | CapturedActivityAssertions |
| NativeCaptureReport | ActivityCaptureReport |

`QylTestHooks` was removed: the current Activity carries native trace context.
The separate architecture demonstration and its profiles have moved to
`architecture-template-fixtures`. They are not Qyl telemetry contracts.
The historical `Qyl.Telemetry.Testing` NuGet package is unchanged; this source
repository remains non-packable pending its separate publication design.
