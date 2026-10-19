using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using Grpc.Core;
using Grpc.Net.Client;
using JetBrains.Annotations;

namespace Qyl.Telemetry.IntegrationTests.Receiver;

[TestClass]
public sealed class PayloadLimitTests
{
    [UsedImplicitly] public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DataRow(0)]
    [DataRow(-1)]
    public async Task Nonpositive_limits_are_rejected_before_opening_listeners(int limit)
    {
        var failure = await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() =>
            OtlpReceiver.StartAsync(limit, TestContext.CancellationToken));
        Assert.AreEqual("maxPayloadBytes", failure.ParamName);
    }

    [TestMethod]
    [DataRow(16 * 1024 * 1024, false, false)]
    [DataRow(16 * 1024 * 1024, true, false)]
    [DataRow(16 * 1024 * 1024, true, true)]
    [DataRow(1024, false, false)]
    [DataRow(1024, true, false)]
    [DataRow(1024, true, true)]
    public async Task Http_enforces_the_default_or_configured_limit_with_and_without_content_length(
        int limit, bool gzip, bool chunked)
    {
        await using var receiver = limit == 16 * 1024 * 1024
            ? await OtlpReceiver.StartAsync(TestContext.CancellationToken)
            : await OtlpReceiver.StartAsync(limit, TestContext.CancellationToken);
        foreach (var size in new[] { limit - 1, limit, limit + 1 })
        {
            var payload = UnknownFieldPayload(size);
            using var content = Content(gzip ? Compress(payload) : payload, gzip, chunked);
            if (chunked) Assert.IsNull(content.Headers.ContentLength);
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(receiver.Endpoint, "v1/traces"));
            request.Content = content;
            // Let the declared-size rejection return its OTLP error before a large eager upload.
            request.Headers.ExpectContinue = !chunked;
            using var response = await TelemetryLifecycle.Http.SendAsync(request, TestContext.CancellationToken);
            Assert.AreEqual(size > limit ? HttpStatusCode.RequestEntityTooLarge : HttpStatusCode.OK, response.StatusCode, $"Payload size: {size}; gzip: {gzip}; chunked: {chunked}.");
            if (size <= limit)
            {
                Assert.IsEmpty(await response.Content.ReadAsByteArrayAsync(TestContext.CancellationToken));
                Assert.AreSequenceEqual(payload, receiver.Snapshot().Requests[^1].Payload.ToArray());
            }
            else
            {
                var error = await TransportTests.ReadErrorAsync(response, TestContext.CancellationToken);
                Assert.AreEqual((int)Google.Rpc.Code.ResourceExhausted, error.Code);
                Assert.Contains($"{limit}-byte", error.Message);
                Assert.AreEqual(error.Message, receiver.Snapshot().Receipts[^1].Error);
            }
        }
        var snapshot = receiver.Snapshot();
        Assert.HasCount(2, snapshot.Requests);
        Assert.HasCount(3, snapshot.Receipts);
        Assert.IsTrue(snapshot.Receipts.Take(2).All(static receipt => receipt.Error is null));
        Assert.IsEmpty(snapshot.Spans);
    }

    [TestMethod]
    public async Task Chunked_identity_requests_enforce_the_native_wire_budget()
    {
        await using var receiver = await OtlpReceiver.StartAsync(1024, TestContext.CancellationToken);
        // Kestrel counts chunk framing against its wire budget. Leave room for that framing
        // in the accepted request; the rejected payload exceeds the budget by itself.
        // https://github.com/dotnet/aspnetcore/blob/v10.0.0/src/Servers/Kestrel/Core/src/Internal/Http/Http1ChunkedEncodingMessageBody.cs
        foreach (var size in new[] { 992, 1025 })
        {
            using var content = Content(UnknownFieldPayload(size), false, true);
            Assert.IsNull(content.Headers.ContentLength);
            using var response = await TelemetryLifecycle.Http.PostAsync(new Uri(receiver.Endpoint, "v1/metrics"),
                content, TestContext.CancellationToken);
            Assert.AreEqual(size == 992 ? HttpStatusCode.OK : HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
            if (size == 992) continue;
            var error = await TransportTests.ReadErrorAsync(response, TestContext.CancellationToken);
            Assert.AreEqual((int)Google.Rpc.Code.ResourceExhausted, error.Code);
            Assert.Contains("1024-byte", error.Message);
            Assert.AreEqual(error.Message, receiver.Snapshot().Receipts[^1].Error);
        }
        Assert.HasCount(1, receiver.Snapshot().Requests);
        Assert.HasCount(2, receiver.Snapshot().Receipts);
    }

    [TestMethod]
    public async Task Compressed_wire_overflow_without_content_length_returns_an_otlp_error()
    {
        await using var receiver = await OtlpReceiver.StartAsync(1024, TestContext.CancellationToken);
        var gzip = Compress([0xfa, 0x07, 0x00]);
        var body = new byte[gzip.Length + 1026];
        gzip.AsSpan(0, 10).CopyTo(body);
        body[3] |= 0x08; // FNAME: a NUL-terminated header that emits no decoded bytes.
        body.AsSpan(10, 1025).Fill((byte)'x');
        gzip.AsSpan(10).CopyTo(body.AsSpan(1036));
        using var content = Content(body, true, true);
        Assert.IsNull(content.Headers.ContentLength);
        using var response = await TelemetryLifecycle.Http.PostAsync(new Uri(receiver.Endpoint, "v1/logs"),
            content, TestContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        var error = await TransportTests.ReadErrorAsync(response, TestContext.CancellationToken);
        Assert.AreEqual((int)Google.Rpc.Code.ResourceExhausted, error.Code);
        Assert.Contains("1024-byte", error.Message);
        Assert.IsEmpty(receiver.Snapshot().Requests);
        Assert.AreEqual(error.Message, Assert.ContainsSingle(receiver.Snapshot().Receipts).Error);
    }

    [TestMethod]
    public async Task Trace_capture_applies_the_configured_limit_to_both_transports()
    {
        var trace = await TelemetryCapture.TraceAsync(async capture =>
        {
            using var channel = GrpcChannel.ForAddress(capture.GrpcEndpoint);
            var bytes = Marshallers.Create<byte[]>(static (value, context) => context.Complete(value),
                static context => context.PayloadAsNewBuffer());
            var method = new Method<byte[], byte[]>(MethodType.Unary,
                "opentelemetry.proto.collector.trace.v1.TraceService", "Export", bytes, bytes);
            using var call = channel.CreateCallInvoker().AsyncUnaryCall(method, null,
                new CallOptions(cancellationToken: TestContext.CancellationToken), new byte[129]);
            var failure = await Assert.ThrowsExactlyAsync<RpcException>(() => call.ResponseAsync);
            Assert.AreEqual(StatusCode.ResourceExhausted, failure.StatusCode);
            Assert.Contains("128-byte", failure.Status.Detail);
            using var content = Content(new byte[129], false, false);
            using var response = await TelemetryLifecycle.Http.PostAsync(new Uri(capture.Endpoint, "v1/traces"),
                content, TestContext.CancellationToken);
            Assert.AreEqual(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        }, result => result.Capture.Receipts.Count == 1, maxPayloadBytes: 128,
            cancellationToken: TestContext.CancellationToken);
        Assert.IsEmpty(trace.Capture.Requests);
        Assert.IsEmpty(trace.Spans);
        var error = Assert.ContainsSingle(trace.Capture.Receipts).Error;
        Assert.IsNotNull(error);
        Assert.Contains("128-byte", error);
    }

    private static byte[] UnknownFieldPayload(int size)
    {
        var payload = new byte[size];
        payload[0] = 0xfa; payload[1] = 0x07;
        var length = size - 6;
        for (var i = 0; i < 4; i++) payload[2 + i] = (byte)((length >> (7 * i)) | (i < 3 ? 0x80 : 0));
        return payload;
    }

    private static byte[] Compress(byte[] payload)
    {
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.Fastest, true)) gzip.Write(payload);
        return buffer.ToArray();
    }

    private static HttpContent Content(byte[] payload, bool gzip, bool chunked)
    {
        HttpContent content = chunked ? new UnknownLengthContent(payload) : new ByteArrayContent(payload);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/x-protobuf");
        if (gzip) content.Headers.ContentEncoding.Add("gzip");
        return content;
    }

    private sealed class UnknownLengthContent(byte[] payload) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(payload).AsTask();
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken) =>
            stream.WriteAsync(payload, cancellationToken).AsTask();
    }
}
