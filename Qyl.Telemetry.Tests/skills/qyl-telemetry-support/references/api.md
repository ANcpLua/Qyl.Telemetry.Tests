# Support APIs

| Capability | Implementation |
| --- | --- |
| Independent native MSTest trace for each invocation and data row | [TracePerTestAttribute](../../../TracePerTestAttribute.cs) |
| Telemetry obligations verified after a passing test | [Telemetry policies](telemetry-policies.md), [Policies](../../../Policies) |
| Capture one operation with an owned OTLP receiver | [TelemetryCapture.TraceAsync and CaptureContext](../../../TelemetryCapture.cs) |
| Typed evidence, trace selection, parentage, calls and store identities | [TraceEvidence](../../../TraceEvidence.cs) |
| Exact captured arrays, including duplicates and empty strings | [Assert.That.HasCapturedValues](../../../CapturedAttributeAssertions.cs) |
| One native span and preserved parent context | [SpanAssertions](../../../SpanAssertions.cs) |
| The OTLP/HTTP error body (google.rpc.Status) of a failed export | [Assert.That.HasOtlpErrorAsync](../../../OtlpErrorAssertions.cs) |
| Raw CLR array, parent/trace identity and disposal checks in native applications | [CapturedActivityAssertions](../../../../Qyl.Telemetry.Tests.Capture/CapturedActivityAssertions.cs) |
| Detached Activity snapshots and source-filtered export | [ActivitySnapshot](../../../../Qyl.Telemetry.Tests.Capture/ActivitySnapshot.cs), [ActivitySnapshotExporter](../../../../Qyl.Telemetry.Tests.Capture/ActivitySnapshotExporter.cs) |
| Versioned HTTP/gRPC fixture JSON | [ActivityCaptureReport](../../../../Qyl.Telemetry.Tests.Capture/ActivityCaptureReport.cs) |
| Protocol and registry conformance | [OtlpReceiver](../../../OtlpReceiver.cs), [RegistryConformance](../../../RegistryConformance.cs) |
| OpenTelemetry SDK exporter aimed at a test receiver over OTLP/HTTP or gRPC | [AddOtlpCapture](../../../OtlpCaptureExporter.cs) |
| OTLP export requests built from records, and the fully populated reference span | [OtlpEvidence](../../../Evidence/OtlpEvidence.cs) |
| Isolated published Collector process and cleanup | [CollectorFixture](../../../Collector/CollectorFixture.cs) |
| Managed or native executable identity and integrity | [PublishedExecutable](../../../Collector/PublishedExecutable.cs) |
| Collector ingestion and investigation | [CollectorClient](../../../Collector/CollectorClient.cs) |
| Capture, forwarding, persistence and diagnostic files | [TelemetryPipeline](../../../Collector/TelemetryPipeline.cs) |
| Instrumented child application and complete delivery fixture | [InstrumentedApplicationFixture](../../../Collector/InstrumentedApplicationFixture.cs) |
| One request to the instrumented application and where its server span sits | [RoundTripAsync, Assert.That.HasServerSpanUnderTheTestClient](../../../Collector/ApplicationRoundTrip.cs) |

`[TracePerTest]` wraps the original method attribute once per invocation and data
row, then verifies declared policies. `TelemetryPipeline` uses the invocation
TestContext workspace and attaches its evidence files on disposal.
`InvocationContractTests` runs a real child runner to check rows, outcomes,
source locations and policy verdicts.
