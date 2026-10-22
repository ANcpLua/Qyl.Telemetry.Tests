using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using JetBrains.Annotations;

namespace Qyl.Telemetry.IntegrationTests.Receiver;

[TestClass]
public sealed class TransportTests
{
    [UsedImplicitly] public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DataRow("unknown", "application/x-protobuf", null, HttpStatusCode.NotFound)]
    [DataRow(Signals.Traces, "application/json", null, HttpStatusCode.UnsupportedMediaType)]
    [DataRow(Signals.Traces, null, null, HttpStatusCode.UnsupportedMediaType)]
    [DataRow(Signals.Traces, "application/x-protobuf", "br", HttpStatusCode.UnsupportedMediaType)]
    [DataRow(Signals.Traces, "application/x-protobuf; charset=utf-8", null, HttpStatusCode.OK)]
    public async Task HttpReceiver_RouteMediaTypeAndEncoding_ReturnTheExactStatus(string signal, string? media,
        string? encoding, HttpStatusCode expected)
    {
        await using var receiver = await OtlpReceiver.StartAsync(TestContext.CancellationToken);
        using var content = new ByteArrayContent([]);
        content.Headers.ContentType = media is null ? null : MediaTypeHeaderValue.Parse(media);
        if (encoding is not null) content.Headers.ContentEncoding.Add(encoding);
        using var response = await TelemetryLifecycle.Http.PostAsync(new Uri(receiver.Endpoint, "v1/" + signal),
            content, TestContext.CancellationToken);
        Assert.AreEqual(expected, response.StatusCode);
        if (expected != HttpStatusCode.OK)
        {
            var error = await Assert.HasOtlpErrorAsync(response, TestContext.CancellationToken,
                media == "application/json" ? "application/json" : "application/x-protobuf");
            Assert.AreEqual(expected == HttpStatusCode.NotFound ? (int)Google.Rpc.Code.NotFound : (int)Google.Rpc.Code.Unimplemented, error.Code);
            Assert.IsFalse(string.IsNullOrWhiteSpace(error.Message));
            Assert.AreEqual(error.Message, Assert.ContainsSingle(receiver.Snapshot().Receipts).Error);
        }
    }

    [TestMethod]
    [DataRow("application/x-protobuf ; charset=utf-8")]
    [DataRow("application/x-protobuf\t;charset=utf-8")]
    public async Task HttpReceiver_MediaTypeWithWhitespaceBeforeItsParameter_IsAccepted(string contentType)
    {
        await using var receiver = await OtlpReceiver.StartAsync(TestContext.CancellationToken);
        using var connection = new TcpClient();
        await connection.ConnectAsync(receiver.Endpoint.Host, receiver.Endpoint.Port, TestContext.CancellationToken);
        var stream = connection.GetStream();
        var unnormalizedRequestHead = $"POST /v1/traces HTTP/1.1\r\nHost: {receiver.Endpoint.Authority}\r\n"
            + $"Content-Type: {contentType}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes(unnormalizedRequestHead), TestContext.CancellationToken);
        using var reader = new StreamReader(stream, System.Text.Encoding.ASCII);
        Assert.AreEqual("HTTP/1.1 200 OK", await reader.ReadLineAsync(TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task HttpReceiver_DecompressedRequestOverLimit_IsRejectedBeforeProtobufParsing()
    {
        await using var receiver = await OtlpReceiver.StartAsync(TestContext.CancellationToken);
        using var response = await OtlpHttp.PostAsync(receiver.Endpoint, OtlpHttp.Compress(new byte[16 * 1024 * 1024 + 1]),
            TestContext.CancellationToken, Signals.Traces, "gzip");
        Assert.AreEqual(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        var error = await Assert.HasOtlpErrorAsync(response, TestContext.CancellationToken);
        Assert.AreEqual((int)Google.Rpc.Code.ResourceExhausted, error.Code);
        Assert.Contains("16777216-byte", error.Message);
        Assert.AreEqual(error.Message, Assert.ContainsSingle(receiver.Snapshot().Receipts).Error);
        Assert.IsEmpty(receiver.Snapshot().Spans);
    }

    [TestMethod]
    public async Task HttpReceiver_InvalidGzip_ReturnsBadRequestWithItsErrorRecord()
    {
        await using var receiver = await OtlpReceiver.StartAsync(TestContext.CancellationToken);
        using var response = await OtlpHttp.PostAsync(receiver.Endpoint, [1, 2, 3], TestContext.CancellationToken,
            Signals.Logs, "gzip");
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        var receipt = Assert.ContainsSingle(receiver.Snapshot().Receipts);
        Assert.AreEqual(Signals.Logs, receipt.Signal);
        Assert.IsFalse(string.IsNullOrEmpty(receipt.Error));
        Assert.AreEqual(0L, receipt.Accepted);
        var error = await Assert.HasOtlpErrorAsync(response, TestContext.CancellationToken);
        Assert.AreEqual((int)Google.Rpc.Code.InvalidArgument, error.Code);
        Assert.AreEqual(receipt.Error, error.Message);
    }

    [TestMethod]
    public async Task HttpReceiver_DeclaredOversizeRequest_ReturnsADecodableResourceExhaustedError()
    {
        await using var receiver = await OtlpReceiver.StartAsync(TestContext.CancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(receiver.Endpoint, "v1/traces"));
        request.Content = new ByteArrayContent(new byte[16 * 1024 * 1024 + 1]);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-protobuf");
        const bool negotiateDeclaredLengthBeforeUploadToAvoidABrokenPipe = true;
        request.Headers.ExpectContinue = negotiateDeclaredLengthBeforeUploadToAvoidABrokenPipe;
        using var response = await TelemetryLifecycle.Http.SendAsync(request, TestContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        var error = await Assert.HasOtlpErrorAsync(response, TestContext.CancellationToken);
        Assert.AreEqual((int)Google.Rpc.Code.ResourceExhausted, error.Code);
        Assert.IsFalse(string.IsNullOrWhiteSpace(error.Message));
        var snapshot = receiver.Snapshot();
        Assert.IsEmpty(snapshot.Spans);
        Assert.AreEqual(error.Message, Assert.ContainsSingle(snapshot.Receipts).Error);
    }
}
