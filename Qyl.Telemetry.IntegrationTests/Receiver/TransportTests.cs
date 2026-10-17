using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using JetBrains.Annotations;
using Google.Protobuf;
using RpcStatus = Google.Rpc.Status;

namespace Qyl.Telemetry.IntegrationTests.Receiver;

[TestClass]
public sealed class TransportTests
{
    [UsedImplicitly] public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DataRow("unknown", "application/x-protobuf", null, HttpStatusCode.NotFound)]
    [DataRow("traces", "application/json", null, HttpStatusCode.UnsupportedMediaType)]
    [DataRow("traces", null, null, HttpStatusCode.UnsupportedMediaType)]
    [DataRow("traces", "application/x-protobuf", "br", HttpStatusCode.UnsupportedMediaType)]
    [DataRow("traces", "application/x-protobuf; charset=utf-8", null, HttpStatusCode.OK)]
    public async Task Receiver_routes_and_media_contract_return_the_exact_status(string signal, string? media,
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
            var error = await ReadErrorAsync(response, TestContext.CancellationToken,
                media == "application/json" ? "application/json" : "application/x-protobuf");
            Assert.AreEqual(expected == HttpStatusCode.NotFound ? (int)Google.Rpc.Code.NotFound : (int)Google.Rpc.Code.Unimplemented, error.Code);
            Assert.IsFalse(string.IsNullOrWhiteSpace(error.Message));
            Assert.AreEqual(error.Message, Assert.ContainsSingle(receiver.Snapshot().Receipts).Error);
        }
    }

    [TestMethod]
    public async Task Decompressed_request_limit_is_enforced_before_protobuf_parsing()
    {
        await using var receiver = await OtlpReceiver.StartAsync(TestContext.CancellationToken);
        using var bytes = new MemoryStream();
        await using (var gzip = new GZipStream(bytes, CompressionLevel.SmallestSize, true))
        {
            await gzip.WriteAsync(new byte[16 * 1024 * 1024 + 1], TestContext.CancellationToken);
        }

        using var content = new ByteArrayContent(bytes.ToArray());
        content.Headers.ContentType = new MediaTypeHeaderValue("application/x-protobuf");
        content.Headers.ContentEncoding.Add("gzip");
        using var response = await TelemetryLifecycle.Http.PostAsync(new Uri(receiver.Endpoint, "v1/traces"), content,
            TestContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        var error = await ReadErrorAsync(response, TestContext.CancellationToken);
        Assert.AreEqual((int)Google.Rpc.Code.ResourceExhausted, error.Code);
        Assert.Contains("16777216-byte", error.Message);
        Assert.AreEqual(error.Message, Assert.ContainsSingle(receiver.Snapshot().Receipts).Error);
        Assert.IsEmpty(receiver.Snapshot().Spans);
    }

    [TestMethod]
    public async Task Invalid_gzip_is_reported_as_bad_request_with_its_error_record()
    {
        await using var receiver = await OtlpReceiver.StartAsync(TestContext.CancellationToken);
        using var content = new ByteArrayContent([1, 2, 3]);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/x-protobuf");
        content.Headers.ContentEncoding.Add("gzip");
        using var response = await TelemetryLifecycle.Http.PostAsync(new Uri(receiver.Endpoint, "v1/logs"), content,
            TestContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        var receipt = Assert.ContainsSingle(receiver.Snapshot().Receipts);
        Assert.AreEqual("logs", receipt.Signal);
        Assert.IsFalse(string.IsNullOrEmpty(receipt.Error));
        Assert.AreEqual(0L, receipt.Accepted);
        var error = await ReadErrorAsync(response, TestContext.CancellationToken);
        Assert.AreEqual((int)Google.Rpc.Code.InvalidArgument, error.Code);
        Assert.AreEqual(receipt.Error, error.Message);
    }

    [TestMethod]
    public async Task Declared_oversize_request_returns_a_decodable_resource_error()
    {
        await using var receiver = await OtlpReceiver.StartAsync(TestContext.CancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(receiver.Endpoint, "v1/traces"));
        request.Content = new ByteArrayContent(new byte[16 * 1024 * 1024 + 1]);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-protobuf");
        // Negotiate the declared length before upload; an eager HTTP/1.1 client can observe a
        // broken pipe when Kestrel closes an already-rejected oversized body.
        request.Headers.ExpectContinue = true;
        using var response = await TelemetryLifecycle.Http.SendAsync(request, TestContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        var error = await ReadErrorAsync(response, TestContext.CancellationToken);
        Assert.AreEqual((int)Google.Rpc.Code.ResourceExhausted, error.Code);
        Assert.IsFalse(string.IsNullOrWhiteSpace(error.Message));
        var snapshot = receiver.Snapshot();
        Assert.IsEmpty(snapshot.Spans);
        Assert.AreEqual(error.Message, Assert.ContainsSingle(snapshot.Receipts).Error);
    }

    internal static async Task<RpcStatus> ReadErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken,
        string mediaType = "application/x-protobuf")
    {
        Assert.AreEqual(mediaType, response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        Assert.IsNotEmpty(body);
        return mediaType == "application/json"
            ? JsonParser.Default.Parse<RpcStatus>(System.Text.Encoding.UTF8.GetString(body))
            : RpcStatus.Parser.ParseFrom(body);
    }
}
