using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Time.Testing;
using Qyl.Api.Contracts;
using Qyl.Api.Contracts.Common.Pagination;
using Qyl.Api.Contracts.OTel.Metrics;
using Qyl.Telemetry.Tests.Collector;

namespace Qyl.Telemetry.UnitTests.Collector;

/// <summary>Checks complete stored metric-series reads and their bounded evidence wait.</summary>
/// <param name="testContext">The runner context supplies cancellation.</param>
[TestClass]
[TestCategory("Fast")]
public class CollectorMetricSeriesTests(TestContext testContext)
{
    private const string MetricName = "queue.depth";
    private const string Repeated = "repeated";
    private const string ExpectedSeries = "expected";

    [TestMethod]
    public async Task Should_ReturnEverySeriesInOrder_When_CollectorMetricSeries_SpansTwoPages()
    {
        var (series, _) = await ReadTwoPagesAsync(testContext.CancellationToken);
        Assert.AreSequenceEqual(["first", "second"], series.Select(item => item.SeriesId));
    }

    [TestMethod]
    public async Task Should_EncodeTheNameInTheRoute_When_CollectorMetricSeries_NameHasReservedCharacters()
    {
        var (_, requests) = await ReadTwoPagesAsync(testContext.CancellationToken);
        Assert.AreEqual("/api/v1/metrics/queue.depth%2B%26/series", requests[0].AbsolutePath);
    }

    [TestMethod]
    public async Task Should_EncodeTheCursorInTheQuery_When_CollectorMetricSeries_CursorHasReservedCharacters()
    {
        var (_, requests) = await ReadTwoPagesAsync(testContext.CancellationToken);
        Assert.Contains("cursor=next%20%2B%26", requests[1].Query);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow(Repeated)]
    public async Task Should_ThrowInsteadOfTruncating_When_CollectorMetricSeries_ContinuationIsMissingEmptyOrRepeated(string? cursor)
    {
        using var client = Client(_ => Json(ContinuationPage(cursor)));
        var failure = await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            new CollectorClient(client).MetricSeriesAsync(MetricName, testContext.CancellationToken));
        Assert.AreEqual("qyl returned an invalid metric series continuation cursor.", failure.Message);
    }

    [TestMethod]
    public async Task Should_RejectExplicitNull_When_CollectorMetricSeries_CursorViolatesContract()
    {
        using var client = Client(_ => Json("""{"items":[],"has_more":true,"next_cursor":null}"""));
        _ = await Assert.ThrowsExactlyAsync<JsonException>(() =>
            new CollectorClient(client).MetricSeriesAsync(MetricName, testContext.CancellationToken));
    }

    [TestMethod]
    public async Task Should_StopReading_When_CollectorMetricSeries_CallerCancelsDuringPagination()
    {
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(testContext.CancellationToken);
        _ = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            ReadTwoPagesAsync(caller.Token, caller.Cancel));
    }

    [TestMethod]
    public async Task Should_ReturnMatchingSeries_When_WaitForMetricSeries_ConditionMatchesAfterOnePollingInterval()
    {
        var clock = new FakeTimeProvider();
        using var collector = new FakeCollector([Page("other"), Page(ExpectedSeries)]);
        var waiting = collector.Client(clock).WaitForMetricSeriesAsync(MetricName,
            items => items.Any(item => item.SeriesId == ExpectedSeries), testContext.CancellationToken);
        await collector.FirstRead.WaitAsync(testContext.CancellationToken);
        clock.Advance(CollectorClient.PollingInterval);
        var series = await waiting.WaitAsync(testContext.CancellationToken);
        Assert.AreEqual(ExpectedSeries, Assert.ContainsSingle(series).SeriesId);
    }

    [TestMethod]
    public async Task Should_ThrowTimeoutException_When_WaitForMetricSeries_MetricMissingUntilTheDeadline()
    {
        var clock = new FakeTimeProvider();
        using var collector = new FakeCollector([null]);
        var waiting = collector.Client(clock).WaitForMetricSeriesAsync(MetricName, _ => true, testContext.CancellationToken);
        await collector.FirstRead.WaitAsync(testContext.CancellationToken);
        clock.Advance(CollectorClient.WaitDeadline);
        var failure = await Assert.ThrowsExactlyAsync<TimeoutException>(() => waiting);
        Assert.AreEqual($"Collector did not return the required evidence for series for metric '{MetricName}'.", failure.Message);
    }

    [TestMethod]
    public async Task Should_ThrowOperationCanceledException_When_WaitForMetricSeries_CallerCancelsBeforeTheDeadline()
    {
        var clock = new FakeTimeProvider();
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(testContext.CancellationToken);
        using var collector = new FakeCollector([null]);
        var waiting = collector.Client(clock).WaitForMetricSeriesAsync(MetricName, _ => true, caller.Token);
        await collector.FirstRead.WaitAsync(testContext.CancellationToken);
        await caller.CancelAsync();
        _ = await Assert.ThrowsAsync<OperationCanceledException>(() => waiting);
    }

    private static async Task<(IReadOnlyList<MetricSeries> Series, List<Uri> Requests)> ReadTwoPagesAsync(
        CancellationToken cancellationToken, Action? onSecondPage = null)
    {
        var requests = new List<Uri>();
        using var client = Client(request =>
        {
            requests.Add(request.RequestUri!);
            if (requests.Count == 2) onSecondPage?.Invoke();
            return Json(requests.Count == 1 ? Page("first", "next +&") : Page("second"));
        });
        var series = await new CollectorClient(client).MetricSeriesAsync("queue.depth+&", cancellationToken);
        return (series, requests);
    }

    private static string Page(string seriesId, string? nextCursor = null) =>
        ContractJson.Serialize(new CursorPageMetricSeries
        {
            Items = [new MetricSeries
            {
                SeriesId = seriesId,
                Name = MetricName,
                Kind = MetricKind.Gauge,
                Attributes = [],
                FirstSeen = "2026-10-23T00:00:00Z",
                LastSeen = "2026-10-23T00:00:00Z"
            }],
            HasMore = nextCursor is not null,
            NextCursor = nextCursor
        }).GetRawText();

    private static string ContinuationPage(string? cursor)
    {
        var page = new JsonObject { ["items"] = new JsonArray(), ["has_more"] = true };
        if (cursor is not null) page["next_cursor"] = cursor;
        return page.ToJsonString();
    }

    private static HttpResponseMessage Json(string text) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(text, Encoding.UTF8, "application/json")
    };

    private static HttpClient Client(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new(new StubHandler(respond)) { BaseAddress = new Uri("https://qyl.test/") };
}
