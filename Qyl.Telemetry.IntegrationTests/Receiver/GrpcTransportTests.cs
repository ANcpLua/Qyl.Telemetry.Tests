using System.Net;
using System.Net.Http.Headers;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using JetBrains.Annotations;
using OpenTelemetry.Proto.Collector.Logs.V1;
using OpenTelemetry.Proto.Collector.Metrics.V1;
using OpenTelemetry.Proto.Collector.Trace.V1;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Logs.V1;
using OpenTelemetry.Proto.Metrics.V1;
using OpenTelemetry.Proto.Resource.V1;
using OpenTelemetry.Proto.Trace.V1;
using ProtoLog = OpenTelemetry.Proto.Logs.V1.LogRecord;

namespace Qyl.Telemetry.IntegrationTests.Receiver;

[TestClass]
public sealed class GrpcTransportTests
{
    private static readonly TimeProvider Clock = TimeProvider.System;

    [UsedImplicitly] public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DataRow(Signals.Traces, false)]
    [DataRow(Signals.Logs, false)]
    [DataRow(Signals.Metrics, false)]
    [DataRow(Signals.Traces, true)]
    [DataRow(Signals.Logs, true)]
    [DataRow(Signals.Metrics, true)]
    public async Task GrpcReceiver_UnknownFieldsNoncanonicalVarintsAndGzip_KeepTheOriginalMessage(string signal, bool gzip)
    {
        await using var receiver = await OtlpReceiver.StartAsync(TestContext.CancellationToken);
        using var wire = new WireHeaders();
        using var channel = GrpcChannel.ForAddress(receiver.GrpcEndpoint, new GrpcChannelOptions { HttpHandler = wire });
        byte[] unknownFieldsDescendingWithNonMinimalVarint1 = [0xf8, 0x07, 0x81, 0x00, 0xf0, 0x07, 0x02];
        var headers = new Metadata();
        if (gzip) headers.Add("grpc-internal-encoding-request", "gzip");
        using var call = channel.CreateCallInvoker().AsyncUnaryCall(Export(signal), null,
            new CallOptions(headers, cancellationToken: TestContext.CancellationToken), unknownFieldsDescendingWithNonMinimalVarint1);
        Assert.IsEmpty(await call.ResponseAsync);
        Assert.AreEqual(gzip ? "gzip" : null, wire.Encoding);
        var snapshot = receiver.Snapshot();
        Assert.IsEmpty(snapshot.Spans);
        Assert.IsEmpty(snapshot.Logs);
        Assert.IsEmpty(snapshot.Metrics);
        var receipt = Assert.ContainsSingle(snapshot.Receipts);
        Assert.AreEqual(OtlpTransport.Grpc, receipt.Transport);
        Assert.AreEqual(signal, receipt.Signal);
        Assert.AreEqual(0L, receipt.Accepted);
        Assert.AreEqual(0L, receipt.Rejected);
        Assert.IsNull(receipt.Error);
        var original = Assert.ContainsSingle(snapshot.Requests);
        Assert.AreEqual(OtlpTransport.Grpc, original.Transport);
        Assert.AreEqual(signal, original.Signal);
        Assert.AreSequenceEqual(unknownFieldsDescendingWithNonMinimalVarint1, original.Payload.ToArray());
    }

    [TestMethod]
    [DataRow(Signals.Traces)]
    [DataRow(Signals.Logs)]
    [DataRow(Signals.Metrics)]
    public async Task GrpcReceiver_MalformedProtobuf_ReturnsInvalidArgumentAndKeepsTheOriginal(string signal)
    {
        await using var receiver = await OtlpReceiver.StartAsync(TestContext.CancellationToken);
        using var channel = GrpcChannel.ForAddress(receiver.GrpcEndpoint);
        using var call = channel.CreateCallInvoker().AsyncUnaryCall(Export(signal), null,
            new CallOptions(cancellationToken: TestContext.CancellationToken), [0x80]);
        var failure = await Assert.ThrowsExactlyAsync<RpcException>(() => call.ResponseAsync);
        Assert.AreEqual(StatusCode.InvalidArgument, failure.StatusCode);
        Assert.IsFalse(string.IsNullOrWhiteSpace(failure.Status.Detail));
        var snapshot = receiver.Snapshot();
        var receipt = Assert.ContainsSingle(snapshot.Receipts);
        Assert.AreEqual(failure.Status.Detail, receipt.Error);
        Assert.AreEqual(OtlpTransport.Grpc, receipt.Transport);
        Assert.AreEqual(0L, receipt.Accepted);
        Assert.AreEqual(0L, receipt.Rejected);
        Assert.AreSequenceEqual(new byte[] { 0x80 }, Assert.ContainsSingle(snapshot.Requests).Payload.ToArray());
    }

    [TestMethod]
    [DataRow(Signals.Traces)]
    [DataRow(Signals.Logs)]
    [DataRow(Signals.Metrics)]
    public async Task GrpcReceiver_PartiallyInvalidExport_KeepsValidRecordsReportsExactRejectionsAndRecovers(string signal)
    {
        await using var receiver = await OtlpReceiver.StartAsync(TestContext.CancellationToken);
        using var channel = GrpcChannel.ForAddress(receiver.GrpcEndpoint);
        var bytes = Request(signal, true, false).ToByteArray();
        using var call = channel.CreateCallInvoker().AsyncUnaryCall(Export(signal), null,
            new CallOptions(cancellationToken: TestContext.CancellationToken), bytes);
        var acknowledgement = Signals.PartialSuccess(signal, await call.ResponseAsync);
        Assert.IsNotNull(acknowledgement);
        var (rejected, message) = acknowledgement.Value;
        Assert.AreEqual(1L, rejected);
        Assert.IsFalse(string.IsNullOrWhiteSpace(message));
        var snapshot = receiver.Snapshot();
        var receipt = Assert.ContainsSingle(snapshot.Receipts);
        Assert.AreEqual(1L, receipt.Accepted);
        Assert.AreEqual(1L, receipt.Rejected);
        Assert.AreEqual(message, receipt.Error);
        Assert.AreEqual(OtlpTransport.Grpc, receipt.Transport);
        Assert.AreEqual(1, snapshot.Spans.Count + snapshot.Logs.Count + snapshot.Metrics.Count);
        Assert.AreSequenceEqual(bytes, Assert.ContainsSingle(snapshot.Requests).Payload.ToArray());
        using var callAfterRejection = channel.CreateCallInvoker().AsyncUnaryCall(Export(signal), null,
            new CallOptions(cancellationToken: TestContext.CancellationToken), []);
        Assert.IsEmpty(await callAfterRejection.ResponseAsync);
        Assert.IsNull(receiver.Snapshot().Receipts[1].Error);
        Assert.HasCount(1, snapshot.Receipts);
    }

    [TestMethod]
    [DataRow(Signals.Traces)]
    [DataRow(Signals.Logs)]
    [DataRow(Signals.Metrics)]
    public async Task GrpcReceiver_ValidRecordWithWarning_IsAcknowledgedWithoutARejection(string signal)
    {
        await using var receiver = await OtlpReceiver.StartAsync(TestContext.CancellationToken);
        using var channel = GrpcChannel.ForAddress(receiver.GrpcEndpoint);
        using var call = channel.CreateCallInvoker().AsyncUnaryCall(Export(signal), null,
            new CallOptions(cancellationToken: TestContext.CancellationToken), Request(signal, false, true).ToByteArray());
        var acknowledgement = Signals.PartialSuccess(signal, await call.ResponseAsync);
        Assert.IsNotNull(acknowledgement);
        var (rejected, message) = acknowledgement.Value;
        Assert.AreEqual(0L, rejected);
        Assert.Contains("StringValueStrindex 7", message);
        var receipt = Assert.ContainsSingle(receiver.Snapshot().Receipts);
        Assert.AreEqual(1L, receipt.Accepted);
        Assert.IsNull(receipt.Error);
        Assert.AreEqual(message, Assert.ContainsSingle(receipt.Warnings));
    }

    [TestMethod]
    [DataRow(1024, false)]
    [DataRow(1024, true)]
    [DataRow(16 * 1024 * 1024, false)]
    [DataRow(16 * 1024 * 1024, true)]
    public async Task GrpcReceiver_DecompressedMessageOverLimit_IsRejectedBeforeDecoding(int limit, bool gzip)
    {
        await using var receiver = limit == 16 * 1024 * 1024
            ? await OtlpReceiver.StartAsync(TestContext.CancellationToken)
            : await OtlpReceiver.StartAsync(limit, TestContext.CancellationToken);
        using var channel = GrpcChannel.ForAddress(receiver.GrpcEndpoint);
        var headers = new Metadata();
        if (gzip) headers.Add("grpc-internal-encoding-request", "gzip");
        using var call = channel.CreateCallInvoker().AsyncUnaryCall(Export(Signals.Traces), null,
            new CallOptions(headers, cancellationToken: TestContext.CancellationToken), new byte[limit + 1]);
        var failure = await Assert.ThrowsExactlyAsync<RpcException>(() => call.ResponseAsync);
        Assert.AreEqual(StatusCode.ResourceExhausted, failure.StatusCode, failure.Status.Detail);
        Assert.Contains($"{limit}-byte", failure.Status.Detail);
        Assert.IsEmpty(receiver.Snapshot().Requests);
        Assert.IsEmpty(receiver.Snapshot().Spans);
    }

    [TestMethod]
    [DataRow(1024, false)]
    [DataRow(1024, true)]
    [DataRow(16 * 1024 * 1024, false)]
    [DataRow(16 * 1024 * 1024, true)]
    public async Task GrpcReceiver_MessageExactlyAtDecompressedLimit_IsAccepted(int limit, bool gzip)
    {
        await using var receiver = limit == 16 * 1024 * 1024
            ? await OtlpReceiver.StartAsync(TestContext.CancellationToken)
            : await OtlpReceiver.StartAsync(limit, TestContext.CancellationToken);
        using var channel = GrpcChannel.ForAddress(receiver.GrpcEndpoint);
        var bytes = new byte[limit];
        byte[] unknownLengthDelimitedField127Tag = [0xfa, 0x07];
        const int fourByteLengthVarint = 4;
        unknownLengthDelimitedField127Tag.CopyTo(bytes, 0);
        var length = bytes.Length - unknownLengthDelimitedField127Tag.Length - fourByteLengthVarint;
        for (var i = 0; i < fourByteLengthVarint; i++)
            bytes[unknownLengthDelimitedField127Tag.Length + i] = (byte)((length >> (7 * i)) | (i < fourByteLengthVarint - 1 ? 0x80 : 0));
        var headers = new Metadata();
        if (gzip) headers.Add("grpc-internal-encoding-request", "gzip");
        using var call = channel.CreateCallInvoker().AsyncUnaryCall(Export(Signals.Traces), null,
            new CallOptions(headers, cancellationToken: TestContext.CancellationToken), bytes);
        Assert.IsEmpty(await call.ResponseAsync);
        Assert.AreSequenceEqual(bytes, Assert.ContainsSingle(receiver.Snapshot().Requests).Payload.ToArray());
        Assert.IsNull(Assert.ContainsSingle(receiver.Snapshot().Receipts).Error);
    }

    [TestMethod]
    public async Task GrpcReceiver_IncompleteUploadCancelled_KeepsNoPartialCaptureAndStaysUsable()
    {
        await using var receiver = await OtlpReceiver.StartAsync(TestContext.CancellationToken);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        cancellation.CancelAfter(TimeSpan.FromSeconds(10));
        using var content = new PausedGrpcContent(cancellation.Token);
        using var request = new HttpRequestMessage(HttpMethod.Post,
            new Uri(receiver.GrpcEndpoint, "opentelemetry.proto.collector.trace.v1.TraceService/Export"));
        request.Version = HttpVersion.Version20;
        request.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
        request.Content = content;
        request.Headers.TE.Add(new TransferCodingWithQualityHeaderValue("trailers"));
        var sending = TelemetryLifecycle.Http.SendAsync(request, cancellation.Token);
        var first = await Task.WhenAny(content.Started.Task, sending);
        var wasSuspended = first == content.Started.Task && !sending.IsCompleted;
        await cancellation.CancelAsync();
        content.Resume.TrySetResult();
        await Assert.ThrowsAsync<OperationCanceledException>(() => sending);
        Assert.IsTrue(wasSuspended);
        using var channel = GrpcChannel.ForAddress(receiver.GrpcEndpoint);
        using var next = channel.CreateCallInvoker().AsyncUnaryCall(Export(Signals.Traces), null,
            new CallOptions(cancellationToken: TestContext.CancellationToken), []);
        Assert.IsEmpty(await next.ResponseAsync);
        Assert.IsEmpty(Assert.ContainsSingle(receiver.Snapshot().Requests).Payload.ToArray());
        Assert.IsNull(Assert.ContainsSingle(receiver.Snapshot().Receipts).Error);
    }

    [TestMethod]
    public async Task GrpcReceiver_DeadlineExpired_CreatesNoExportEvidence()
    {
        await using var receiver = await OtlpReceiver.StartAsync(TestContext.CancellationToken);
        using var channel = GrpcChannel.ForAddress(receiver.GrpcEndpoint);
        using var call = channel.CreateCallInvoker().AsyncUnaryCall(Export(Signals.Traces), null,
            new CallOptions(deadline: Clock.GetUtcNow().AddSeconds(-1).UtcDateTime, cancellationToken: TestContext.CancellationToken), []);
        var failure = await Assert.ThrowsExactlyAsync<RpcException>(() => call.ResponseAsync);
        Assert.AreEqual(StatusCode.DeadlineExceeded, failure.StatusCode);
        Assert.IsEmpty(receiver.Snapshot().Requests);
    }

    [TestMethod]
    public async Task OtlpReceiver_Disposed_ClosesBothListenersToFreshConnectionsAndWakesAPendingWait()
    {
        var receiver = await OtlpReceiver.StartAsync(TestContext.CancellationToken);
        using var channel = GrpcChannel.ForAddress(receiver.GrpcEndpoint);
        using (var live = channel.CreateCallInvoker().AsyncUnaryCall(Export(Signals.Traces), null,
                   new CallOptions(cancellationToken: TestContext.CancellationToken), []))
            Assert.IsEmpty(await live.ResponseAsync);
        var waiting = receiver.AwaitAsync(snapshot => snapshot.Spans.Count > 0, TimeSpan.FromSeconds(10), TestContext.CancellationToken);
        await receiver.DisposeAsync();
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => waiting);
        using var freshChannelToClosedListener = GrpcChannel.ForAddress(receiver.GrpcEndpoint);
        using var closed = freshChannelToClosedListener.CreateCallInvoker().AsyncUnaryCall(Export(Signals.Traces), null,
            new CallOptions(deadline: Clock.GetUtcNow().AddSeconds(5).UtcDateTime, cancellationToken: TestContext.CancellationToken), []);
        var failure = await Assert.ThrowsExactlyAsync<RpcException>(() => closed.ResponseAsync);
        Assert.AreEqual(StatusCode.Unavailable, failure.StatusCode, failure.Status.Detail);
        await Assert.ThrowsExactlyAsync<HttpRequestException>(() => TelemetryLifecycle.Http.GetAsync(receiver.Endpoint, TestContext.CancellationToken));
    }

    private static Method<byte[], byte[]> Export(string signal) => new(MethodType.Unary, signal switch
        {
            Signals.Traces => "opentelemetry.proto.collector.trace.v1.TraceService",
            Signals.Logs => "opentelemetry.proto.collector.logs.v1.LogsService",
            _ => "opentelemetry.proto.collector.metrics.v1.MetricsService"
        }, "Export",
        Marshallers.Create<byte[]>(static (bytes, context) => context.Complete(bytes), static context => context.PayloadAsNewBuffer()),
        Marshallers.Create<byte[]>(static (bytes, context) => context.Complete(bytes), static context => context.PayloadAsNewBuffer()));

    private static IMessage Request(string signal, bool invalid, bool warning)
    {
        var resource = new Resource { Attributes = { new KeyValue { Key = "custom.value", Value = warning
            ? new AnyValue { StringValueStrindex = 7 } : new AnyValue { IntValue = 9_007_199_254_740_993L } } } };
        switch (signal)
        {
            case Signals.Traces:
                var spans = new ScopeSpans { Scope = new InstrumentationScope { Name = "grpc.trace" } };
                spans.Spans.Add(new Span { TraceId = ByteString.CopyFrom(Enumerable.Repeat((byte)1, 16).ToArray()),
                    SpanId = ByteString.CopyFrom(Enumerable.Repeat((byte)2, 8).ToArray()), Name = "preserved" });
                if (invalid) spans.Spans.Add(new Span { Name = "bad-identity" });
                return new ExportTraceServiceRequest { ResourceSpans = { new ResourceSpans { Resource = resource, ScopeSpans = { spans } } } };
            case Signals.Logs:
                var logs = new ScopeLogs { LogRecords = { new ProtoLog { Body = new AnyValue { BoolValue = false } } } };
                if (invalid) logs.LogRecords.Add(new ProtoLog { TraceId = ByteString.CopyFrom([1]) });
                return new ExportLogsServiceRequest { ResourceLogs = { new ResourceLogs { Resource = resource, ScopeLogs = { logs } } } };
            default:
                var metrics = new ScopeMetrics { Metrics = { new Metric { Name = "grpc.counter", Sum = new Sum
                    { IsMonotonic = true, AggregationTemporality = AggregationTemporality.Delta,
                        DataPoints = { new NumberDataPoint { TimeUnixNano = 10, AsInt = 7 } } } } } };
                if (invalid) metrics.Metrics.Add(new Metric { Name = "bad-attributes", Gauge = new Gauge
                    { DataPoints = { new NumberDataPoint { TimeUnixNano = 10, AsInt = 9, Attributes =
                        { new KeyValue { Key = "same", Value = new AnyValue { IntValue = 1 } },
                            new KeyValue { Key = "same", Value = new AnyValue { IntValue = 2 } } } } } } });
                return new ExportMetricsServiceRequest { ResourceMetrics = { new ResourceMetrics { Resource = resource, ScopeMetrics = { metrics } } } };
        }
    }

    private sealed class WireHeaders() : DelegatingHandler(new SocketsHttpHandler())
    {
        internal string? Encoding { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Encoding = request.Headers.TryGetValues("grpc-encoding", out var values) ? values.Single() : null;
            return base.SendAsync(request, cancellationToken);
        }
    }

    private sealed class PausedGrpcContent : HttpContent
    {
        internal readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationToken _operationCancellation;
        internal PausedGrpcContent(CancellationToken operationCancellation)
        {
            _operationCancellation = operationCancellation;
            Headers.ContentType = new MediaTypeHeaderValue("application/grpc");
        }
        protected override bool TryComputeLength(out long length) { length = 7; return true; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => WriteAsync(stream, CancellationToken.None);
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken) => WriteAsync(stream, cancellationToken);
        private async Task WriteAsync(Stream stream, CancellationToken cancellationToken)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _operationCancellation);
            cancellationToken = linked.Token;
            // A unary frame declaring two bytes, paused after the first payload byte.
            await stream.WriteAsync(new byte[] { 0, 0, 0, 0, 2, 0x80 }, cancellationToken);
            await stream.FlushAsync(cancellationToken);
            Started.TrySetResult();
            await Resume.Task.WaitAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            await stream.WriteAsync(new byte[] { 0 }, cancellationToken);
        }
    }
}
