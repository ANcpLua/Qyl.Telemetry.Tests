using System.Net;
using System.Text;
using System.Text.Json;
using Qyl.Telemetry.Tests.Collector;
using Qyl.Api.Contracts.OTel.Metrics;
using StoredLogRecord = Qyl.Api.Contracts.OTel.Logs.LogRecord;

namespace Qyl.Telemetry.UnitTests.Collector;

/// <summary>Checks qyl query pagination, wire failures and explicit metric windows.</summary>
/// <param name="testContext">The runner context supplies cancellation.</param>
[TestClass]
public sealed class CollectorClientTests(TestContext testContext)
{
    private const string Repeated = "repeated";
    private const string ExportRoute = "v1/traces";
    private static readonly DateTimeOffset WindowStart = new(2026, 10, 16, 12, 0, 0, TimeSpan.FromHours(2));

    [TestMethod]
    public async Task CollectorClientLogs_ContinuationCursor_ReadsEveryPage()
    {
        var (_, queries) = await ReadTwoLogPagesAsync();
        Assert.HasCount(2, queries);
    }

    [TestMethod]
    public async Task CollectorClientLogs_TwoEmptyPages_ReturnNoRecords()
    {
        var (logs, _) = await ReadTwoLogPagesAsync();
        Assert.IsEmpty(logs);
    }

    [TestMethod]
    public async Task CollectorClientLogs_TraceIdWithReservedCharacters_IsEncodedInTheQuery()
    {
        var (_, queries) = await ReadTwoLogPagesAsync();
        Assert.Contains("trace_id=trace%26one", queries[0]);
    }

    [TestMethod]
    public async Task CollectorClientLogs_CursorWithReservedCharacters_IsEncodedInTheNextQuery()
    {
        var (_, queries) = await ReadTwoLogPagesAsync();
        Assert.Contains("cursor=next%20%2B%26", queries[1]);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow(Repeated)]
    public async Task CollectorClientLogs_MissingEmptyOrRepeatedContinuation_ThrowsInsteadOfTruncating(string? cursor)
    {
        using var client = Client(_ => Json(JsonSerializer.Serialize(new { items = Array.Empty<object>(), has_more = true, next_cursor = cursor })));
        var failure = await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            new CollectorClient(client).LogsAsync("trace", testContext.CancellationToken));
        Assert.Contains("continuation cursor", failure.Message);
    }

    [TestMethod]
    public async Task CollectorClientLogs_HttpError_ThrowsHttpRequestException()
    {
        using var failed = Client(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        _ = await Assert.ThrowsExactlyAsync<HttpRequestException>(() => new CollectorClient(failed).LogsAsync("trace", testContext.CancellationToken));
    }

    [TestMethod]
    public async Task CollectorClientLogs_NullJson_ThrowsInvalidDataException()
    {
        using var empty = Client(_ => Json("null"));
        _ = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => new CollectorClient(empty).LogsAsync("trace", testContext.CancellationToken));
    }

    [TestMethod]
    public async Task CollectorClientMetricQuery_P95Aggregation_IsSentInTheQuery()
    {
        var (query, _) = await QueryP95MetricAsync();
        Assert.Contains("aggregation=p95", query);
    }

    [TestMethod]
    public async Task CollectorClientMetricQuery_OffsetStartTime_IsSentAsUtc()
    {
        var (query, _) = await QueryP95MetricAsync();
        Assert.Contains("start_time=2026-10-16T10%3A00%3A00", query);
    }

    [TestMethod]
    public async Task CollectorClientMetricQuery_Response_KeepsTheMetricName()
    {
        var (_, result) = await QueryP95MetricAsync();
        Assert.AreEqual(MetricResponse.Name, result.Name);
    }

    [TestMethod]
    public async Task CollectorClientMetricQuery_Response_KeepsTheAggregation()
    {
        var (_, result) = await QueryP95MetricAsync();
        Assert.AreEqual(MetricResponse.Aggregation, result.Aggregation);
    }

    [TestMethod]
    public async Task CollectorClientMetricQuery_TruncatedResponse_StaysTruncated()
    {
        var (_, result) = await QueryP95MetricAsync();
        Assert.IsTrue(result.Truncated);
    }

    [TestMethod]
    public async Task CollectorClientMetricQuery_BlankName_ThrowsArgumentException()
    {
        using var client = Client(_ => Json(JsonSerializer.Serialize(MetricResponse)));
        _ = await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
            new CollectorClient(client).QueryMetricAsync(" ", WindowStart, WindowStart, cancellationToken: testContext.CancellationToken));
    }

    [TestMethod]
    [DataRow(HttpStatusCode.OK, false)]
    [DataRow(HttpStatusCode.OK, true)]
    [DataRow(HttpStatusCode.BadRequest, false)]
    public async Task CollectorClientExport_CollectorReply_IsForwardedWithItsStatus(HttpStatusCode status, bool nonemptyAcknowledgement)
    {
        using var collector = Client(_ => new HttpResponseMessage(status)
        {
            Content = new ByteArrayContent(nonemptyAcknowledgement ? NonemptyAcknowledgement : [])
        });
        using var reply = await ExportAsync(collector);
        Assert.AreEqual(status, reply.StatusCode);
    }

    [TestMethod]
    public async Task CollectorClientExport_NonemptyAcknowledgement_IsForwardedByteForByte()
    {
        using var partial = Client(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(NonemptyAcknowledgement) });
        using var reply = await ExportAsync(partial);
        Assert.AreSequenceEqual(NonemptyAcknowledgement, await reply.Content.ReadAsByteArrayAsync(testContext.CancellationToken));
    }

    private static byte[] NonemptyAcknowledgement => [.. "\n\0"u8];

    private static MetricQueryResult MetricResponse => new()
    {
        Name = "http.server.request.duration",
        Kind = MetricKind.Histogram,
        Unit = "s",
        Aggregation = MetricAggregation.P95,
        StartTime = WindowStart,
        EndTime = WindowStart.AddMinutes(1),
        StepMs = 1000,
        NumericRepresentation = "float64",
        Approximate = true,
        Series = [],
        Truncated = true
    };

    private async Task<(IReadOnlyList<StoredLogRecord> Logs, List<string> Queries)> ReadTwoLogPagesAsync()
    {
        var queries = new List<string>();
        using var client = Client(request =>
        {
            queries.Add(request.RequestUri!.Query);
            return Json(queries.Count == 1
                ? """{"items":[],"has_more":true,"next_cursor":"next +&"}"""
                : """{"items":[],"has_more":false}""");
        });
        var logs = await new CollectorClient(client).LogsAsync("trace&one", testContext.CancellationToken);
        return (logs, queries);
    }

    private async Task<(string Query, MetricQueryResult Result)> QueryP95MetricAsync()
    {
        var query = "";
        using var client = Client(request =>
        {
            query = request.RequestUri!.Query;
            return Json(JsonSerializer.Serialize(MetricResponse));
        });
        var result = await new CollectorClient(client).QueryMetricAsync(MetricResponse.Name, WindowStart, WindowStart.AddMinutes(1),
            MetricAggregation.P95, testContext.CancellationToken);
        return (query, result);
    }

    private Task<HttpResponseMessage> ExportAsync(HttpClient client) =>
        new CollectorClient(client).ExportAsync(ExportRoute, ReadOnlyMemory<byte>.Empty, OtlpReceiver.ProtobufMediaType, null, testContext.CancellationToken);

    private static HttpResponseMessage Json(string text) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(text, Encoding.UTF8, "application/json")
    };

    private static HttpClient Client(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new(new StubHandler(respond)) { BaseAddress = new Uri("https://qyl.test/") };
}
