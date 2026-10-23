using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Qyl.Api.Contracts;
using Qyl.Telemetry.Tests.Collector;
using Qyl.Api.Contracts.OTel.Metrics;
using StoredLogRecord = Qyl.Api.Contracts.OTel.Logs.LogRecord;

namespace Qyl.Telemetry.UnitTests.Collector;

/// <summary>Checks qyl query pagination, wire failures and explicit metric windows.</summary>
/// <param name="testContext">The runner context supplies cancellation.</param>
[TestClass]
[TestCategory("Fast")]
public class CollectorClientTests(TestContext testContext)
{
    private const string Repeated = "repeated";
    private const string ExportRoute = "v1/traces";
    private static readonly DateTimeOffset s_windowStart = new(2026, 10, 16, 12, 0, 0, TimeSpan.FromHours(2));

    [TestMethod]
    public async Task Should_ReadEveryPage_When_CollectorClientLogs_ContinuationCursor()
    {
        var (_, queries) = await ReadTwoLogPagesAsync();
        Assert.HasCount(2, queries);
    }

    [TestMethod]
    public async Task Should_ReturnNoRecords_When_CollectorClientLogs_TwoEmptyPages()
    {
        var (logs, _) = await ReadTwoLogPagesAsync();
        Assert.IsEmpty(logs);
    }

    [TestMethod]
    public async Task Should_BeEncodedInTheQuery_When_CollectorClientLogs_TraceIdWithReservedCharacters()
    {
        var (_, queries) = await ReadTwoLogPagesAsync();
        Assert.Contains("trace_id=trace%26one", queries[0]);
    }

    [TestMethod]
    public async Task Should_BeEncodedInTheNextQuery_When_CollectorClientLogs_CursorWithReservedCharacters()
    {
        var (_, queries) = await ReadTwoLogPagesAsync();
        Assert.Contains("cursor=next%20%2B%26", queries[1]);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow(Repeated)]
    public async Task Should_ThrowInsteadOfTruncating_When_CollectorClientLogs_MissingEmptyOrRepeatedContinuation(string? cursor)
    {
        using var client = Client(_ => Json(ContinuationPage(cursor)));
        var failure = await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            new CollectorClient(client).LogsAsync("trace", testContext.CancellationToken));
        Assert.Contains("continuation cursor", failure.Message);
    }

    [TestMethod]
    public async Task Should_RejectExplicitNull_When_CollectorClientLogs_CursorViolatesContract()
    {
        using var client = Client(_ => Json("""{"items":[],"has_more":true,"next_cursor":null}"""));
        _ = await Assert.ThrowsExactlyAsync<JsonException>(() => new CollectorClient(client).LogsAsync("trace", testContext.CancellationToken));
    }

    [TestMethod]
    public async Task Should_ThrowHttpRequestException_When_CollectorClientLogs_HttpError()
    {
        using var failed = Client(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        _ = await Assert.ThrowsExactlyAsync<HttpRequestException>(() => new CollectorClient(failed).LogsAsync("trace", testContext.CancellationToken));
    }

    [TestMethod]
    public async Task Should_ThrowInvalidDataException_When_CollectorClientLogs_NullJson()
    {
        using var empty = Client(_ => Json("null"));
        _ = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => new CollectorClient(empty).LogsAsync("trace", testContext.CancellationToken));
    }

    [TestMethod]
    public async Task Should_BeSentInTheQuery_When_CollectorClientMetricQuery_P95Aggregation()
    {
        var (query, _) = await QueryP95MetricAsync();
        Assert.Contains("aggregation=p95", query);
    }

    [TestMethod]
    public async Task Should_BeSentAsUtc_When_CollectorClientMetricQuery_OffsetStartTime()
    {
        var (query, _) = await QueryP95MetricAsync();
        Assert.Contains("start_time=2026-10-16T10%3A00%3A00", query);
    }

    [TestMethod]
    public async Task Should_KeepTheMetricName_When_CollectorClientMetricQuery_Response()
    {
        var (_, result) = await QueryP95MetricAsync();
        Assert.AreEqual(MetricResponse.Name, result.Name);
    }

    [TestMethod]
    public async Task Should_KeepTheAggregation_When_CollectorClientMetricQuery_Response()
    {
        var (_, result) = await QueryP95MetricAsync();
        Assert.AreEqual(MetricResponse.Aggregation, result.Aggregation);
    }

    [TestMethod]
    public async Task Should_StayTruncated_When_CollectorClientMetricQuery_TruncatedResponse()
    {
        var (_, result) = await QueryP95MetricAsync();
        Assert.IsTrue(result.Truncated);
    }

    [TestMethod]
    public async Task Should_PreserveTimestampText_When_CollectorClientMetricQuery_HasNanosecondsAndOffset()
    {
        const string Timestamp = "2026-10-23t12:34:56.123456789+02:30";
        var body = MetricBody();
        body["start_time"] = Timestamp;
        var result = await ReadMetricAsync(body);
        Assert.AreEqual(Timestamp, result.StartTime);
    }

    [TestMethod]
    public async Task Should_RejectTimestamp_When_CollectorClientMetricQuery_HasInvalidCalendarDate()
    {
        var body = MetricBody();
        body["start_time"] = "2026-02-30T12:00:00Z";
        _ = await Assert.ThrowsExactlyAsync<JsonException>(() => ReadMetricAsync(body));
    }

    [TestMethod]
    public async Task Should_RejectQuotedNumber_When_CollectorClientMetricQuery_HasStringStep()
    {
        var body = MetricBody();
        body["step_ms"] = "1000";
        _ = await Assert.ThrowsExactlyAsync<JsonException>(() => ReadMetricAsync(body));
    }

    [TestMethod]
    public async Task Should_PreserveOptionalPresence_When_CollectorClientMetricQuery_HasNullableAggregate()
    {
        var body = MetricBody();
        body["series"] = JsonNode.Parse("""[{"attributes":[],"buckets":[]},{"attributes":[],"buckets":[],"value":null},{"attributes":[],"buckets":[],"value":0}]""");
        var result = await ReadMetricAsync(body);
        Assert.HasCount(3, result.Series);
        Assert.IsFalse(result.Series[0].Value.IsDefined);
        Assert.IsTrue(result.Series[1].Value.IsDefined);
        Assert.IsNull(result.Series[1].Value.Value);
        Assert.IsTrue(result.Series[2].Value.IsDefined);
        Assert.AreEqual(0d, result.Series[2].Value.Value);
    }

    [TestMethod]
    public async Task Should_ThrowArgumentException_When_CollectorClientMetricQuery_BlankName()
    {
        using var client = Client(_ => Json(ContractJson.Serialize(MetricResponse).GetRawText()));
        _ = await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
            new CollectorClient(client).QueryMetricAsync(" ", s_windowStart, s_windowStart, cancellationToken: testContext.CancellationToken));
    }

    [TestMethod]
    [DataRow(HttpStatusCode.OK, false)]
    [DataRow(HttpStatusCode.OK, true)]
    [DataRow(HttpStatusCode.BadRequest, false)]
    public async Task Should_BeForwardedWithItsStatus_When_CollectorClientExport_CollectorReply(HttpStatusCode status, bool nonemptyAcknowledgement)
    {
        using var collector = Client(_ => Acknowledgement(status, nonemptyAcknowledgement));
        using var reply = await ExportAsync(collector);
        Assert.AreEqual(status, reply.StatusCode);
    }

    [TestMethod]
    public async Task Should_BeForwardedByteForByte_When_CollectorClientExport_NonemptyAcknowledgement()
    {
        using var partial = Client(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(NonemptyAcknowledgement) });
        using var reply = await ExportAsync(partial);
        Assert.AreSequenceEqual(NonemptyAcknowledgement, await reply.Content.ReadAsByteArrayAsync(testContext.CancellationToken));
    }

    private static byte[] NonemptyAcknowledgement => [.. "\n\0"u8];

    private static HttpResponseMessage Acknowledgement(HttpStatusCode status, bool nonempty) => new(status)
    {
        Content = new ByteArrayContent(nonempty ? NonemptyAcknowledgement : [])
    };

    private static MetricQueryResult MetricResponse => new()
    {
        Name = "http.server.request.duration",
        Kind = MetricKind.Histogram,
        Unit = "s",
        Aggregation = MetricAggregation.P95,
        StartTime = ContractJson.FormatTimestamp(s_windowStart),
        EndTime = ContractJson.FormatTimestamp(s_windowStart.AddMinutes(1)),
        StepMs = 1000,
        NumericRepresentation = "float64",
        Approximate = true,
        Series = [],
        Truncated = true
    };

    private static JsonObject MetricBody() => JsonNode.Parse(ContractJson.Serialize(MetricResponse).GetRawText())!.AsObject();

    private static string ContinuationPage(string? cursor)
    {
        var page = new JsonObject { ["items"] = new JsonArray(), ["has_more"] = true };
        if (cursor is not null) page["next_cursor"] = cursor;
        return page.ToJsonString();
    }

    private async Task<MetricQueryResult> ReadMetricAsync(JsonObject body)
    {
        using var client = Client(_ => Json(body.ToJsonString()));
        return await new CollectorClient(client).QueryMetricAsync(MetricResponse.Name, s_windowStart,
            s_windowStart.AddMinutes(1), cancellationToken: testContext.CancellationToken);
    }

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
            return Json(ContractJson.Serialize(MetricResponse).GetRawText());
        });
        var result = await new CollectorClient(client).QueryMetricAsync(MetricResponse.Name, s_windowStart, s_windowStart.AddMinutes(1),
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
