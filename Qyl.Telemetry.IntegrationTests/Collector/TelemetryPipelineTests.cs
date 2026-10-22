using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Qyl.Telemetry.Tests.Collector;
using Google.Protobuf;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Features;
using Qyl.Api.Contracts.Common;
using ContractKind = Qyl.Api.Contracts.OTel.Enums.SpanKind;
using RpcStatus = Google.Rpc.Status;

namespace Qyl.Telemetry.IntegrationTests.Collector;

/// <summary>Checks real SDK capture, persisted records and the forwarding HTTP boundary.</summary>
/// <param name="testContext">The runner owns cancellation and evidence attachments.</param>
[TestClass]
[DoNotParallelize]
public sealed class TelemetryPipelineTests(TestContext testContext) : IAsyncDisposable
{
    private TelemetryPipeline Recorder { get; } = new(testContext);

    private static readonly string[] Checkpoints = ["first", "second"];

    [TestMethod]
    public async Task TelemetryPipeline_CompletedRootWithHttpChild_IsStoredByTheCollector()
    {
        await Recorder.StartAsync(testContext.CancellationToken);
        using var root = Recorder.Begin(nameof(TelemetryPipeline_CompletedRootWithHttpChild_IsStoredByTheCollector));
        var traceId = root.TraceId.ToHexString();
        var rootId = root.SpanId.ToHexString();
        using (Recorder.Ending(root))
        {
            using var response = await TelemetryLifecycle.Http.GetAsync(new Uri(Recorder.Collector.Address, "health"), testContext.CancellationToken);
            _ = response.EnsureSuccessStatusCode();
        }
        var spans = await Recorder.Collector.Client.WaitForSpansAsync(traceId, [rootId], testContext.CancellationToken);
        var ids = spans.Select(span => span.SpanId).ToHashSet();
        Assert.Contains(span => span.SpanId == rootId && span.Name == TracePerTestAttribute.ActivityName, spans);
        Assert.Contains(span => span.Kind == ContractKind.Client && span.ParentSpanId == rootId, spans);
        Assert.IsTrue(spans.All(span => string.IsNullOrEmpty(span.ParentSpanId) || ids.Contains(span.ParentSpanId)));
        testContext.WriteLine(JsonSerializer.Serialize(spans));
    }

    [TestMethod]
    public async Task TelemetryPipeline_RecordedException_IsCapturedAndStoredAsSpanEvent()
    {
        await Recorder.StartAsync(testContext.CancellationToken);
        using var root = Recorder.Begin(nameof(TelemetryPipeline_RecordedException_IsCapturedAndStoredAsSpanEvent));
        var traceId = root.TraceId.ToHexString();
        using (Recorder.Ending(root))
        {
            _ = root.RecordFailure(Assert.ThrowsExactly<InvalidOperationException>(() =>
                throw new InvalidOperationException("payment declined")));
        }
        var original = Assert.ContainsSingle(Assert.ContainsSingle(Recorder.SpansOf(traceId)).Events);
        Assert.AreEqual("exception", original.Name);
        Assert.AreEqual("payment declined", original.Attributes["exception.message"]);
        Assert.IsNotNull(original.Attributes["exception.stacktrace"]);
        var events = await Recorder.Collector.Client.WaitForSpanEventsAsync(traceId,
            stored => stored.Any(spanEvent => spanEvent.Name == "exception"), testContext.CancellationToken);
        var exception = events.First(spanEvent => spanEvent.Name == "exception");
        var attributes = exception.Attributes!.ToDictionary(attribute => attribute.Key, attribute => attribute.Value);
        Assert.AreEqual("System.InvalidOperationException", Assert.IsInstanceOfType<AttributeValue.StringValue>(attributes["exception.type"]).Value);
        Assert.AreEqual("payment declined", Assert.IsInstanceOfType<AttributeValue.StringValue>(attributes["exception.message"]).Value);
        Assert.AreEqual(original.Attributes["exception.stacktrace"], Assert.IsInstanceOfType<AttributeValue.StringValue>(attributes["exception.stacktrace"]).Value);
        Assert.IsGreaterThan(0UL, exception.TimeUnixNano);
    }

    [TestMethod]
    public async Task TelemetryPipeline_StoppedTwice_RestartsWithAnotherReceiverAndSdk()
    {
        await Recorder.StartAsync(testContext.CancellationToken);
        await Recorder.StopAsync();
        await Recorder.StopAsync();
        _ = Assert.ThrowsExactly<InvalidOperationException>(() => Recorder.ReceiverAddress);
        _ = Assert.ThrowsExactly<InvalidOperationException>(() => Recorder.Collector);
        _ = Assert.ThrowsExactly<InvalidOperationException>(() => Recorder.Begin("not-listening"));
        await Recorder.StartAsync(testContext.CancellationToken);
        var address = Recorder.ReceiverAddress;
        await Recorder.StartAsync(testContext.CancellationToken);
        Assert.AreEqual(address, Recorder.ReceiverAddress);
        Assert.IsNotEmpty(Recorder.EvidenceFiles);
    }

    [TestMethod]
    public async Task TelemetryPipeline_SdkSpan_KeepsTypedValuesOrderedEventsAndMetadata()
    {
        await Recorder.StartAsync(testContext.CancellationToken);
        using var root = Recorder.Begin(nameof(TelemetryPipeline_SdkSpan_KeepsTypedValuesOrderedEventsAndMetadata));
        var traceId = root.TraceId.ToHexString();
        using (Recorder.Ending(root))
        {
            using (var child = Spans.Work("recording-test"))
            {
                Assert.IsNotNull(child);
                _ = child.SetTag("checkout.sequence", long.MaxValue);
                _ = child.SetTag("checkout.accepted", true);
                _ = child.SetTag("checkout.checkpoints", Checkpoints);
                _ = child.AddEvent(new ActivityEvent("order.created", tags: new ActivityTagsCollection
                {
                    ["checkout.sequence"] = long.MaxValue,
                    ["checkout.accepted"] = true
                }));
                _ = child.AddEvent(new ActivityEvent("order.charged"));
            }
            var captured = Assert.ContainsSingle(Recorder.SpansOf(traceId));
            Assert.AreEqual(root.SpanId.ToHexString(), captured.ParentSpanId);
            Assert.AreEqual(long.MaxValue, captured.GetInt64("checkout.sequence"));
            Assert.IsNull(captured.GetString("checkout.sequence"));
            Assert.IsTrue(Assert.IsInstanceOfType<bool>(captured.Attributes["checkout.accepted"]));
            Assert.AreSequenceEqual(Checkpoints, captured.GetStrings("checkout.checkpoints"));
            Assert.AreSequenceEqual(["order.created", "order.charged"], captured.Events.Select(item => item.Name));
            Assert.AreEqual(long.MaxValue, captured.Events[0].Attributes["checkout.sequence"]);
            Assert.IsTrue(Assert.IsInstanceOfType<bool>(captured.Events[0].Attributes["checkout.accepted"]));
            Assert.IsTrue(captured.Events.All(item => item.TimeUnixNano >= captured.StartTimeUnixNano && item.TimeUnixNano <= captured.EndTimeUnixNano));
            Assert.AreEqual(TelemetryPipeline.TestProcess, captured.Service);
            Assert.IsNotNull(captured.Scope);
            Assert.IsNotNull(captured.Scope.SchemaUrl);
            Assert.IsNotNull(captured.Resource.Attributes["telemetry.sdk.name"]);
            Assert.AreEqual(1U, captured.Flags & 1U);
            Assert.IsGreaterThan(0UL, captured.StartTimeUnixNano);
            Assert.IsGreaterThanOrEqualTo(captured.StartTimeUnixNano, captured.EndTimeUnixNano);
        }
        Assert.HasCount(2, Recorder.SpansOf(traceId));
        Assert.IsEmpty(Recorder.SpansOf("unrecorded-trace"));
    }

    [TestMethod]
    public async Task TelemetryPipeline_NestedInvocationRoots_HaveSeparateTracesAndRestoreTheAmbientParent()
    {
        await Recorder.StartAsync(testContext.CancellationToken);
        using var outer = Recorder.Begin("outer invocation");
        using (Recorder.Ending(outer))
        {
            using var inner = Recorder.Begin("inner invocation");
            using (Recorder.Ending(inner))
            {
                Assert.AreNotEqual(outer.TraceId, inner.TraceId);
                Assert.AreSame(inner, Activity.Current);
            }
            Assert.AreSame(outer, Activity.Current);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void OtlpRelay_MissingListenerAddress_ThrowsAtStartup(bool hasFeature)
    {
        var features = new FeatureCollection();
        if (hasFeature)
        {
            features.Set<IServerAddressesFeature>(new ServerAddressesFeature());
        }
        var failure = Assert.ThrowsExactly<InvalidOperationException>(() => OtlpRelay.BoundAddress(features));
        Assert.AreEqual("The trace receiver has no address.", failure.Message);
    }

    [TestMethod]
    public void OtlpRelay_KestrelBoundAddress_IsReported()
    {
        var features = new FeatureCollection();
        var addresses = new ServerAddressesFeature();
        addresses.Addresses.Add("http://127.0.0.1:43210");
        features.Set<IServerAddressesFeature>(addresses);
        Assert.AreEqual(new Uri("http://127.0.0.1:43210"), OtlpRelay.BoundAddress(features));
    }

    [TestMethod]
    [DataRow(Signals.Traces)]
    [DataRow(Signals.Metrics)]
    [DataRow(Signals.Logs)]
    public async Task OtlpRelay_NonemptyZeroRejectionAcknowledgement_IsRelayedIntact(string signal)
    {
        var acknowledgement = Convert.FromHexString("0A00");
        using var collector = CollectorClient(HttpStatusCode.OK, acknowledgement);
        await using var capture = await OtlpReceiver.StartAsync(testContext.CancellationToken);
        await using var receiver = await OtlpRelay.StartAsync(capture, new CollectorClient(collector), testContext.CancellationToken);
        using var response = await OtlpHttp.PostAsync(receiver.Address, [], testContext.CancellationToken, signal);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("application/x-protobuf", response.Content.Headers.ContentType?.MediaType);
        Assert.AreSequenceEqual(acknowledgement, await response.Content.ReadAsByteArrayAsync(testContext.CancellationToken));
        Assert.AreEqual(signal, Assert.ContainsSingle(capture.Snapshot().Requests).Signal);
        Assert.AreSequenceEqual(acknowledgement, Assert.ContainsSingle(receiver.Deliveries).CollectorResponse);
    }

    [TestMethod]
    public async Task OtlpRelay_MalformedExport_KeepsTheOriginalAndTheUpstreamError()
    {
        var diagnostic = new RpcStatus { Code = 3, Message = "Malformed OTLP request." }.ToByteArray();
        using var collector = CollectorClient(HttpStatusCode.BadRequest, diagnostic);
        await using var capture = await OtlpReceiver.StartAsync(testContext.CancellationToken);
        await using var receiver = await OtlpRelay.StartAsync(capture, new CollectorClient(collector), testContext.CancellationToken);
        using var response = await OtlpHttp.PostAsync(receiver.Address, [0x0b], testContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.AreEqual("Malformed OTLP request.", RpcStatus.Parser.ParseFrom(await response.Content.ReadAsByteArrayAsync(testContext.CancellationToken)).Message);
        var snapshot = capture.Snapshot();
        Assert.IsEmpty(snapshot.Spans);
        Assert.AreSequenceEqual((byte[])[0x0b], Assert.ContainsSingle(snapshot.Requests).Payload.ToArray());
        var delivery = Assert.ContainsSingle(receiver.Deliveries);
        Assert.AreEqual(Signals.Traces, delivery.Signal);
        Assert.AreEqual(400, delivery.CaptureStatus);
        var captureError = RpcStatus.Parser.ParseFrom(delivery.CaptureResponse);
        Assert.AreEqual(3, captureError.Code);
        Assert.IsFalse(string.IsNullOrWhiteSpace(captureError.Message));
        Assert.AreEqual(400, delivery.CollectorStatus);
        Assert.AreSequenceEqual(diagnostic, delivery.CollectorResponse);
    }

    [TestMethod]
    [ResourceLock(WellKnownResources.EnvironmentVariables)]
    public async Task OtlpRelay_InvalidListenerConfiguration_ThrowsFormatException()
    {
        using var collector = CollectorClient(HttpStatusCode.OK, []);
        await using var capture = await OtlpReceiver.StartAsync(testContext.CancellationToken);
        using var setting = new EnvironmentScope(("Kestrel__Endpoints__Broken__Url", "invalid-listener-address"));
        _ = await Assert.ThrowsAsync<FormatException>(() => OtlpRelay.StartAsync(capture, new CollectorClient(collector), testContext.CancellationToken));
    }

    private static HttpClient CollectorClient(HttpStatusCode status, byte[] bytes) =>
        CollectorClient((_, _) => Task.FromResult(Reply(status, bytes)));

    private static HttpClient CollectorClient(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) =>
        new(new StubHandler(respond)) { BaseAddress = new Uri("https://collector.test/") };

    private static HttpResponseMessage Reply(HttpStatusCode status, byte[] bytes) => new(status)
    {
        Content = new ByteArrayContent(bytes) { Headers = { ContentType = new MediaTypeHeaderValue("application/x-protobuf") } }
    };

    [TestMethod]
    [DataRow(Signals.Traces)]
    [DataRow(Signals.Logs)]
    [DataRow(Signals.Metrics)]
    public async Task OtlpRelay_ProducerDisconnectsDuringExport_ForwardsTheAcceptedExport(string signal)
    {
        var pending = new PendingCollector();
        using var collector = pending.Client();
        await using var capture = await OtlpReceiver.StartAsync(testContext.CancellationToken);
        var receiver = await OtlpRelay.StartAsync(capture, new CollectorClient(collector), testContext.CancellationToken);
        using var release = pending.Releasing();
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(testContext.CancellationToken);
        var send = OtlpHttp.PostAsync(receiver.Address, [], cancelled.Token, signal);
        await pending.Started.Task.WaitAsync(testContext.CancellationToken);
        await cancelled.CancelAsync();
        _ = await Assert.ThrowsAsync<OperationCanceledException>(() => send);
        var stop = receiver.DisposeAsync().AsTask();
        Assert.IsFalse(stop.IsCompleted, "Shutdown must join the active export.");
        _ = pending.Complete.TrySetResult();
        await stop;
        var delivery = Assert.ContainsSingle(receiver.Deliveries);
        Assert.AreEqual(200, delivery.CaptureStatus);
        Assert.AreEqual(200, delivery.CollectorStatus);
        Assert.IsNull(delivery.Failure);
        Assert.AreEqual(signal, Assert.ContainsSingle(capture.Snapshot().Requests).Signal);
    }

    [TestMethod]
    public async Task OtlpRelay_UpstreamTransportFailure_IsRecordedInDeliveries()
    {
        var pending = new PendingCollector();
        using var collector = pending.Client();
        await using var capture = await OtlpReceiver.StartAsync(testContext.CancellationToken);
        await using var receiver = await OtlpRelay.StartAsync(capture, new CollectorClient(collector), testContext.CancellationToken);
        var send = OtlpHttp.PostAsync(receiver.Address, [], testContext.CancellationToken, Signals.Logs);
        await pending.Started.Task.WaitAsync(testContext.CancellationToken);
        _ = pending.Complete.TrySetException(new HttpRequestException("upstream unavailable"));
        using var response = await send;
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.AreEqual(14, RpcStatus.Parser.ParseFrom(await response.Content.ReadAsByteArrayAsync(testContext.CancellationToken)).Code);
        var delivery = Assert.ContainsSingle(receiver.Deliveries);
        Assert.AreEqual(200, delivery.CaptureStatus);
        Assert.IsNull(delivery.CollectorStatus);
        Assert.AreEqual(nameof(HttpRequestException), delivery.Failure);
        Assert.AreEqual(Signals.Logs, Assert.ContainsSingle(capture.Snapshot().Requests).Signal);
    }

    /// <summary>A Collector whose export reply waits until the test completes or fails <see cref="Complete" />.</summary>
    private sealed class PendingCollector
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Complete { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completes a pending export on disposal so the relay can join it during cleanup.</summary>
        public Release Releasing() => new(Complete);

        public HttpClient Client() => CollectorClient(async (request, cancellationToken) =>
        {
            _ = Started.TrySetResult();
            await Complete.Task.WaitAsync(cancellationToken);
            return Reply(HttpStatusCode.OK, []);
        });

        public sealed class Release(TaskCompletionSource complete) : IDisposable
        {
            public void Dispose() => _ = complete.TrySetResult();
        }
    }
    public ValueTask DisposeAsync() => Recorder.DisposeAsync();
}
