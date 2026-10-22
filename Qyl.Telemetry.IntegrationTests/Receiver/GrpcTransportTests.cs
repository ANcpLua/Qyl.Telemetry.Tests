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
    [UsedImplicitly] public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DataRow(Signals.Traces, false)]
    [DataRow(Signals.Logs, false)]
    [DataRow(Signals.Metrics, false)]
    [DataRow(Signals.Traces, true)]
    [DataRow(Signals.Logs, true)]
    [DataRow(Signals.Metrics, true)]
    public async Task Original_grpc_message_survives_unknown_fields_noncanonical_varints_and_gzip(string signal, bool gzip)
    {
        await using var receiver = await OtlpReceiver.StartAsync(TestContext.CancellationToken);
        using var wire = new WireHeaders();
        using var channel = GrpcChannel.ForAddress(receiver.GrpcEndpoint, new GrpcChannelOptions { HttpHandler = wire });
        // Unknown fields in descending field order, with a non-minimal varint for 1. Parsing
        // and serializing a generated message would change these authoritative wire bytes.
        byte[] bytes = [0xf8, 0x07, 0x81, 0x00, 0xf0, 0x07, 0x02];
        var headers = new Metadata();
        if (gzip) headers.Add("grpc-internal-encoding-request", "gzip");
        using var call = channel.CreateCallInvoker().AsyncUnaryCall(Export(signal), null,
            new CallOptions(headers, cancellationToken: TestContext.CancellationToken), bytes);
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
        Assert.AreSequenceEqual(bytes, original.Payload.ToArray());
    }

    [TestMethod]
    [DataRow(Signals.Traces)]
    [DataRow(Signals.Logs)]
    [DataRow(Signals.Metrics)]
    public async Task Malformed_grpc_protobuf_returns_invalid_argument_and_retains_original_evidence(string signal)
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
    public async Task Grpc_partial_success_keeps_valid_records_and_reports_exact_rejections(string signal)
    {
        await using var receiver = await OtlpReceiver.StartAsync(TestContext.CancellationToken);
        using var channel = GrpcChannel.ForAddress(receiver.GrpcEndpoint);
        var bytes = Request(signal, true, false).ToByteArray();
        using var call = channel.CreateCallInvoker().AsyncUnaryCall(Export(signal), null,
            new CallOptions(cancellationToken: TestContext.CancellationToken), bytes);
        var (rejected, message) = Ack(signal, await call.ResponseAsync);
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
        // A rejected export must not poison the next call or its diagnostics.
        using var next = channel.CreateCallInvoker().AsyncUnaryCall(Export(signal), null,
            new CallOptions(cancellationToken: TestContext.CancellationToken), []);
        Assert.IsEmpty(await next.ResponseAsync);
        Assert.IsNull(receiver.Snapshot().Receipts[1].Error);
        Assert.HasCount(1, snapshot.Receipts);
    }

    [TestMethod]
    [DataRow(Signals.Traces)]
    [DataRow(Signals.Logs)]
    [DataRow(Signals.Metrics)]
    public async Task Grpc_warning_acknowledges_a_valid_record_without_inventing_a_rejection(string signal)
    {
        await using var receiver = await OtlpReceiver.StartAsync(TestContext.CancellationToken);
        using var channel = GrpcChannel.ForAddress(receiver.GrpcEndpoint);
        using var call = channel.CreateCallInvoker().AsyncUnaryCall(Export(signal), null,
            new CallOptions(cancellationToken: TestContext.CancellationToken), Request(signal, false, true).ToByteArray());
        var (rejected, message) = Ack(signal, await call.ResponseAsync);
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
    public async Task Grpc_bounds_the_decompressed_message_before_decoding(int limit, bool gzip)
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
    public async Task Grpc_accepts_a_valid_message_exactly_at_the_decompressed_limit(int limit, bool gzip)
    {
        await using var receiver = limit == 16 * 1024 * 1024
            ? await OtlpReceiver.StartAsync(TestContext.CancellationToken)
            : await OtlpReceiver.StartAsync(limit, TestContext.CancellationToken);
        using var channel = GrpcChannel.ForAddress(receiver.GrpcEndpoint);
        var bytes = new byte[limit];
        // Unknown length-delimited field 127: two tag bytes, four length bytes, then data.
        bytes[0] = 0xfa; bytes[1] = 0x07;
        var length = bytes.Length - 6;
        for (var i = 0; i < 4; i++) bytes[2 + i] = (byte)((length >> (7 * i)) | (i < 3 ? 0x80 : 0));
        var headers = new Metadata();
        if (gzip) headers.Add("grpc-internal-encoding-request", "gzip");
        using var call = channel.CreateCallInvoker().AsyncUnaryCall(Export(Signals.Traces), null,
            new CallOptions(headers, cancellationToken: TestContext.CancellationToken), bytes);
        Assert.IsEmpty(await call.ResponseAsync);
        Assert.AreSequenceEqual(bytes, Assert.ContainsSingle(receiver.Snapshot().Requests).Payload.ToArray());
        Assert.IsNull(Assert.ContainsSingle(receiver.Snapshot().Receipts).Error);
    }

    [TestMethod]
    public async Task Cancelled_incomplete_grpc_upload_keeps_no_partial_capture_and_receiver_remains_usable()
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
    public async Task Expired_grpc_deadline_does_not_create_export_evidence()
    {
        await using var receiver = await OtlpReceiver.StartAsync(TestContext.CancellationToken);
        using var channel = GrpcChannel.ForAddress(receiver.GrpcEndpoint);
        using var call = channel.CreateCallInvoker().AsyncUnaryCall(Export(Signals.Traces), null,
            new CallOptions(deadline: DateTime.UtcNow.AddSeconds(-1), cancellationToken: TestContext.CancellationToken), []);
        var failure = await Assert.ThrowsExactlyAsync<RpcException>(() => call.ResponseAsync);
        Assert.AreEqual(StatusCode.DeadlineExceeded, failure.StatusCode);
        Assert.IsEmpty(receiver.Snapshot().Requests);
    }

    [TestMethod]
    public async Task Disposal_closes_both_listeners_and_wakes_a_pending_evidence_wait()
    {
        var receiver = await OtlpReceiver.StartAsync(TestContext.CancellationToken);
        try
        {
            using var channel = GrpcChannel.ForAddress(receiver.GrpcEndpoint);
            using (var live = channel.CreateCallInvoker().AsyncUnaryCall(Export(Signals.Traces), null,
                       new CallOptions(cancellationToken: TestContext.CancellationToken), []))
                Assert.IsEmpty(await live.ResponseAsync);
            var waiting = receiver.AwaitAsync(snapshot => snapshot.Spans.Count > 0, TimeSpan.FromSeconds(10), TestContext.CancellationToken);
            await receiver.DisposeAsync();
            await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => waiting);
            // Verify the closed listener with a fresh connection, independently of how the
            // previous HTTP/2 connection reports its graceful shutdown.
            using var disconnected = GrpcChannel.ForAddress(receiver.GrpcEndpoint);
            using var closed = disconnected.CreateCallInvoker().AsyncUnaryCall(Export(Signals.Traces), null,
                new CallOptions(deadline: DateTime.UtcNow.AddSeconds(5), cancellationToken: TestContext.CancellationToken), []);
            var failure = await Assert.ThrowsExactlyAsync<RpcException>(() => closed.ResponseAsync);
            Assert.AreEqual(StatusCode.Unavailable, failure.StatusCode, failure.Status.Detail);
            await Assert.ThrowsExactlyAsync<HttpRequestException>(() => TelemetryLifecycle.Http.GetAsync(receiver.Endpoint, TestContext.CancellationToken));
        }
        finally
        {
            await receiver.DisposeAsync();
        }
    }

    private static Method<byte[], byte[]> Export(string signal) => new(MethodType.Unary, signal switch
        {
            Signals.Traces => "opentelemetry.proto.collector.trace.v1.TraceService",
            Signals.Logs => "opentelemetry.proto.collector.logs.v1.LogsService",
            _ => "opentelemetry.proto.collector.metrics.v1.MetricsService"
        }, "Export",
        Marshallers.Create<byte[]>(static (bytes, context) => context.Complete(bytes), static context => context.PayloadAsNewBuffer()),
        Marshallers.Create<byte[]>(static (bytes, context) => context.Complete(bytes), static context => context.PayloadAsNewBuffer()));

    private static (long Rejected, string Message) Ack(string signal, byte[] bytes)
    {
        switch (signal)
        {
            case Signals.Traces:
                var traces = ExportTraceServiceResponse.Parser.ParseFrom(bytes).PartialSuccess;
                Assert.IsNotNull(traces);
                return (traces.RejectedSpans, traces.ErrorMessage);
            case Signals.Logs:
                var logs = ExportLogsServiceResponse.Parser.ParseFrom(bytes).PartialSuccess;
                Assert.IsNotNull(logs);
                return (logs.RejectedLogRecords, logs.ErrorMessage);
            default:
                var metrics = ExportMetricsServiceResponse.Parser.ParseFrom(bytes).PartialSuccess;
                Assert.IsNotNull(metrics);
                return (metrics.RejectedDataPoints, metrics.ErrorMessage);
        }
    }

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
