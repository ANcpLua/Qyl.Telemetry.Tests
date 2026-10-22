using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Qyl.Telemetry.Tests.Collector;
using Google.Protobuf;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Features;
using Qyl.Api.Contracts.Common;
using RpcStatus = Google.Rpc.Status;

namespace Qyl.Telemetry.IntegrationTests.Collector;

/// <summary>Checks real SDK capture, persisted records and the forwarding HTTP boundary.</summary>
/// <param name="testContext">The runner owns cancellation and evidence attachments.</param>
[TestClass]
[DoNotParallelize]
public sealed class TelemetryPipelineTests(TestContext testContext) : IAsyncDisposable
{
    private TelemetryPipeline Recorder { get; } = new(testContext);

    private readonly HttpClient _client = new();
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
            using var response = await _client.GetAsync(new Uri(Recorder.Collector.Address, "health"), testContext.CancellationToken);
            _ = response.EnsureSuccessStatusCode();
        }
        var trace = await Recorder.Collector.Client.WaitForTraceAsync(traceId,
            stored => stored.GetProperty("spans").EnumerateArray().Any(span => span.GetProperty("span_id").GetString() == rootId),
            testContext.CancellationToken);
        var spans = trace.GetProperty("spans").EnumerateArray().ToArray();
        var ids = spans.Select(span => span.GetProperty("span_id").GetString()).ToHashSet();
        Assert.Contains(span => span.GetProperty("span_id").GetString() == rootId
            && span.GetProperty("name").GetString() == TracePerTestAttribute.ActivityName, spans);
        Assert.Contains(span => span.GetProperty("kind").GetInt32() == 3
            && span.GetProperty("parent_span_id").GetString() == rootId, spans);
        Assert.IsTrue(spans.All(span => !span.TryGetProperty("parent_span_id", out var parent)
            || parent.GetString() is null or "" || ids.Contains(parent.GetString())));
        testContext.WriteLine(trace.GetRawText());
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
        using var body = new ByteArrayContent([]);
        body.Headers.ContentType = new MediaTypeHeaderValue("application/x-protobuf");
        using var response = await _client.PostAsync(new Uri(receiver.Address, $"/v1/{signal}"), body, testContext.CancellationToken);
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
        using var body = new ByteArrayContent([0x0b]);
        body.Headers.ContentType = new MediaTypeHeaderValue("application/x-protobuf");
        using var response = await _client.PostAsync(new Uri(receiver.Address, "/v1/traces"), body, testContext.CancellationToken);
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
    public async Task OtlpRelay_InvalidListenerConfiguration_ThrowsFormatException()
    {
        using var collector = CollectorClient(HttpStatusCode.OK, []);
        await using var capture = await OtlpReceiver.StartAsync(testContext.CancellationToken);
        using var setting = new EnvironmentScope(("Kestrel__Endpoints__Broken__Url", "invalid-listener-address"));
        _ = await Assert.ThrowsAsync<FormatException>(() => OtlpRelay.StartAsync(capture, new CollectorClient(collector), testContext.CancellationToken));
    }

    private static HttpClient CollectorClient(HttpStatusCode status, byte[] bytes) => new(new CollectorHandler(status, bytes))
    {
        BaseAddress = new Uri("https://collector.test/")
    };

    private sealed class CollectorHandler(HttpStatusCode status, byte[] bytes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new ByteArrayContent(bytes) { Headers = { ContentType = new MediaTypeHeaderValue("application/x-protobuf") } }
            });
    }

    [TestMethod]
    [DataRow(Signals.Traces)]
    [DataRow(Signals.Logs)]
    [DataRow(Signals.Metrics)]
    public async Task OtlpRelay_ProducerDisconnectsDuringExport_ForwardsTheAcceptedExport(string signal)
    {
        using var handler = new PendingCollectorHandler();
        using var collector = new HttpClient(handler);
        collector.BaseAddress = new Uri("https://collector.test/");
        await using var capture = await OtlpReceiver.StartAsync(testContext.CancellationToken);
        await using var receiver = await OtlpRelay.StartAsync(capture, new CollectorClient(collector), testContext.CancellationToken);
        using var release = handler.Releasing();
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(testContext.CancellationToken);
        using var body = new ByteArrayContent([]);
        body.Headers.ContentType = new MediaTypeHeaderValue("application/x-protobuf");
        var send = _client.PostAsync(new Uri(receiver.Address, $"v1/{signal}"), body, cancelled.Token);
        await handler.Started.Task.WaitAsync(testContext.CancellationToken);
        await cancelled.CancelAsync();
        _ = await Assert.ThrowsAsync<OperationCanceledException>(() => send);
        var stop = receiver.DisposeAsync().AsTask();
        Assert.IsFalse(stop.IsCompleted, "Shutdown must join the active export.");
        _ = handler.Complete.TrySetResult();
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
        using var handler = new PendingCollectorHandler();
        using var collector = new HttpClient(handler);
        collector.BaseAddress = new Uri("https://collector.test/");
        await using var capture = await OtlpReceiver.StartAsync(testContext.CancellationToken);
        await using var receiver = await OtlpRelay.StartAsync(capture, new CollectorClient(collector), testContext.CancellationToken);
        using var body = new ByteArrayContent([]);
        body.Headers.ContentType = new MediaTypeHeaderValue("application/x-protobuf");
        var send = _client.PostAsync(new Uri(receiver.Address, "v1/logs"), body, testContext.CancellationToken);
        await handler.Started.Task.WaitAsync(testContext.CancellationToken);
        _ = handler.Complete.TrySetException(new HttpRequestException("upstream unavailable"));
        using var response = await send;
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.AreEqual(14, RpcStatus.Parser.ParseFrom(await response.Content.ReadAsByteArrayAsync(testContext.CancellationToken)).Code);
        var delivery = Assert.ContainsSingle(receiver.Deliveries);
        Assert.AreEqual(200, delivery.CaptureStatus);
        Assert.IsNull(delivery.CollectorStatus);
        Assert.AreEqual(nameof(HttpRequestException), delivery.Failure);
        Assert.AreEqual(Signals.Logs, Assert.ContainsSingle(capture.Snapshot().Requests).Signal);
    }

    private sealed class PendingCollectorHandler : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Complete { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completes a pending export on disposal so the relay can join it during cleanup.</summary>
        public Release Releasing() => new(Complete);

        public sealed class Release(TaskCompletionSource complete) : IDisposable
        {
            public void Dispose() => _ = complete.TrySetResult();
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _ = Started.TrySetResult();
            await Complete.Task.WaitAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([]) { Headers = { ContentType = new MediaTypeHeaderValue("application/x-protobuf") } }
            };
        }
    }
    public async ValueTask DisposeAsync()
    {
        using (_client)
        {
            await Recorder.DisposeAsync();
        }
    }
}
