using System.Text.Json;
using System.Text.Json.Nodes;
using OpenTelemetry.Proto.Metrics.V1;
using Qyl.Api.Contracts.Mcp.Tools;
using Qyl.Telemetry.Tests.Evidence;
using static Qyl.Telemetry.EndToEndTests.Analysis.OtlpIncidents;

namespace Qyl.Telemetry.EndToEndTests.Analysis;

/// <summary>Independent incidents whose correct analysis answers the reported engine defects contradict.</summary>
[TestClass]
[DoNotParallelize]
public sealed class EngineDefectTests(TestContext context)
{
    private static readonly SharedAnalysisSession s_shared = new();
    private static ulong s_now;

    private static AnalysisSession Session => s_shared.Session;

    [ClassInitialize]
    [Timeout(300_000, CooperativeCancellation = true)]
    public static async Task StartAsync(TestContext context)
    {
        var session = await s_shared.StartAsync(context);
        s_now = Nanoseconds(session.TimeProvider.GetUtcNow());
    }

    [ClassCleanup]
    public static Task StopAsync() => s_shared.StopAsync();

    [TestMethod]
    [DataRow(AnalysisPath.Http)]
    [DataRow(AnalysisPath.Mcp)]
    public async Task VerifyFix_EnvironmentInTheScopeMatchers_CountsOnlyThatEnvironmentsRequests(AnalysisPath path)
    {
        var service = $"env-{path}".ToLowerInvariant();
        var incidents = EnvironmentIncidents(service);
        await incidents.SendAndWaitAsync(Session.Client, $"{service}-staging-149", context.CancellationToken);
        var failure = Assert.ContainsSingle((await Session.AskAsync<TriageOutput>(path, AnalysisTools.Triage,
            AnswerKey.Window(s_now, ("service_name", service), ("environment", "prod"), ("status", "all")), context.CancellationToken)).Failures);
        Assert.IsNotNull(failure.FailureId);
        var verification = await Session.AskAsync<VerifyFixOutput>(path, AnalysisTools.VerifyFix, AnswerKey.Window(s_now, ("matchers", ScopeMatchers(service, "prod")),
            ("since", AnswerKey.Iso(s_now - 35 * Minute)), ("min_minutes", 1)), context.CancellationToken);
        Assert.AreEqual(0L, verification.After.Requests, "Staging requests must not count for prod.");
        Assert.AreEqual("pending", verification.Verdict);
    }

    [TestMethod]
    [DataRow(AnalysisPath.Http)]
    [DataRow(AnalysisPath.Mcp)]
    public async Task FailureContext_SessionReusingASpanIdAcrossTraces_KeepsEveryReusedSpan(AnalysisPath path)
    {
        var service = $"reuse-{path}".ToLowerInvariant();
        var incidents = new OtlpIncidents();
        var first = incidents.Span($"{service}-a", service, "first", s_now - 10 * Minute, error: true, session: service,
            attributes: new Dictionary<string, object> { ["error.type"] = "reuse" });
        var second = incidents.Span($"{service}-b", service, "second", s_now - 9 * Minute, error: true, session: service, spanId: first.SpanId,
            attributes: new Dictionary<string, object> { ["error.type"] = "reuse" });
        await incidents.SendAndWaitAsync(Session.Client, $"{service}-a", context.CancellationToken);
        await incidents.SendAndWaitAsync(Session.Client, $"{service}-b", context.CancellationToken);
        var output = await Session.AskAsync<GetFailureContextOutput>(path, AnalysisTools.FailureContext, AnswerKey.Window(s_now, ("session_id", service)), context.CancellationToken);
        Assert.AreEqual(first.TraceId, output.Observed.RootCauseSpan?.TraceId);
        Assert.AreSequenceEqual([first.TraceId, second.TraceId],
            output.Observed.ErrorSpans.Select(span => span.TraceId), SequenceOrder.InAnyOrder);
    }

    [TestMethod]
    [DataRow(AnalysisPath.Http)]
    [DataRow(AnalysisPath.Mcp)]
    public async Task CompareGroups_FailureCohort_IsLimitedToItsWindow(AnalysisPath path)
    {
        var service = $"cohort-{path}".ToLowerInvariant();
        var incidents = CohortIncidents(service);
        await incidents.SendAndWaitAsync(Session.Client, $"{service}-us-ok-9", context.CancellationToken);
        var failure = Assert.ContainsSingle((await Session.AskAsync<TriageOutput>(path, AnalysisTools.Triage,
            AnswerKey.Window(s_now, ("service_name", service), ("status", "all")), context.CancellationToken)).Failures);
        var output = await Session.AskAsync<CompareGroupsOutput>(path, AnalysisTools.CompareGroups, AnswerKey.Window(s_now,
            ("matchers", new JsonArray(AnswerKey.Matcher("service.name", "eq", service))),
            ("signal", "spans"), ("group_b", "rest"), ("attribute_scope", "both"),
            ("group_a", new JsonObject
            {
                ["failure_id"] = failure.FailureId,
                ["window"] = new JsonObject { ["start_time"] = AnswerKey.Iso(s_now - 45 * Minute), ["end_time"] = AnswerKey.Iso(s_now - 30 * Minute) }
            })), context.CancellationToken);
        Assert.AreEqual(10L, output.SizeA, "Only the occurrences inside the cohort window belong to it.");
        var cohortRegion = Assert.ContainsSingle(output.Discriminators.Where(association => association.Key == "cloud.region"));
        Assert.AreEqual("eu", cohortRegion.Value.ToString());
        Assert.AreEqual(1d, cohortRegion.ShareA);
    }

    [TestMethod]
    [DataRow(AnalysisPath.Http)]
    [DataRow(AnalysisPath.Mcp)]
    public async Task QueryMetricLast_SeveralSeries_TakesTheLatestPointOfEachBucket(AnalysisPath path)
    {
        var name = $"queue.depth.{path}".ToLowerInvariant();
        var start = (Nanoseconds(Session.TimeProvider.GetUtcNow()) - 10 * Minute) / Minute * Minute;
        var incidents = new OtlpIncidents();
        incidents.Metric("metric-last", new Metric { Name = name, Unit = "{item}", Gauge = QueueGauge(start) });
        await incidents.SendAsync(Session.Client, context.CancellationToken);
        var output = await Session.AskAsync<QueryMetricOutput>(path, AnalysisTools.QueryMetric, new JsonObject
        {
            ["metric_name"] = name, ["aggregation"] = "last", ["step_ms"] = 60_000,
            ["start_time"] = AnswerKey.Iso(start), ["end_time"] = AnswerKey.Iso(start + 2 * Minute)
        }, context.CancellationToken);
        Assert.IsNotNull(output.Series);
        Assert.AreSequenceEqual([7d, 9d], Assert.ContainsSingle(output.Series).Buckets.Select(bucket => bucket.Value ?? double.NaN));
    }

    [TestMethod]
    [DataRow(AnalysisPath.Http)]
    [DataRow(AnalysisPath.Mcp)]
    public async Task QueryMetric_HistogramWithNegativeBounds_KeepsPercentilesWithinThem(AnalysisPath path)
    {
        var name = $"temperature.change.{path}".ToLowerInvariant();
        var incidents = new OtlpIncidents();
        incidents.Metric("negative", new Metric
        {
            Name = name, Unit = "Cel", Histogram = new Histogram
            {
                AggregationTemporality = AggregationTemporality.Delta,
                DataPoints =
                {
                    new HistogramDataPoint
                    {
                        StartTimeUnixNano = s_now - 11 * Minute, TimeUnixNano = s_now - 10 * Minute, Count = 100, Sum = -1600,
                        Min = -20, Max = -12, ExplicitBounds = { -10, -5, 0 }, BucketCounts = { 100, 0, 0, 0 }
                    }
                }
            }
        });
        await incidents.SendAsync(Session.Client, context.CancellationToken);
        var output = await Session.AskAsync<QueryMetricOutput>(path, AnalysisTools.QueryMetric, AnswerKey.Window(s_now, ("metric_name", name), ("aggregation", "p50"),
            ("step_ms", 3_600_000)), context.CancellationToken);
        Assert.IsNotNull(output.Series);
        var median = Assert.ContainsSingle(Assert.ContainsSingle(output.Series).Buckets.Select(bucket => bucket.Value).OfType<double>());
        Assert.IsInRange(-20d, -12d, median);
    }

    [TestMethod]
    [DataRow(AnalysisPath.Http, 400, "cursor.stale")]
    [DataRow(AnalysisPath.Mcp, null, "collector_rejected")]
    public async Task GetTracePaging_SpanSortedAheadOfIssuedCursor_RefusesTheStaleCursorAndAFreshQueryIncludesIt(
        AnalysisPath path, int? expectedStatus, string expectedError)
    {
        var service = $"paging-{path}".ToLowerInvariant();
        var start = s_now - 5 * Minute;
        var incidents = new OtlpIncidents();
        var root = incidents.Span($"{service}-root", service, "root", start + 10 * Second, 30_000);
        var firstChild = incidents.Span($"{service}-child-20", service, "child", start + 20 * Second, 1,
            parent: root.SpanId, trace: root.TraceId);
        var secondChild = incidents.Span($"{service}-child-30", service, "child", start + 30 * Second, 1,
            parent: root.SpanId, trace: root.TraceId);
        await incidents.SendAndWaitAsync(Session.Client, $"{service}-child-30", context.CancellationToken);
        var input = AnswerKey.Window(s_now, ("trace_id", root.TraceId), ("max_spans", 2));
        var page = await Session.AskAsync<JsonElement>(path, AnalysisTools.GetTrace, input, context.CancellationToken);
        Assert.IsTrue(page.GetProperty("has_more").GetBoolean(), "Two of three spans fill the first page.");
        var late = incidents.Span($"{service}-late", service, "late", start + 5_000 * Millisecond, 1, parent: root.SpanId, trace: root.TraceId);
        await incidents.SendAndWaitAsync(Session.Client, $"{service}-late", context.CancellationToken);

        var stale = input.DeepClone().AsObject();
        stale["cursor"] = page.GetProperty("next_cursor").GetString();
        var (status, error) = await Session.AskErrorAsync(path, AnalysisTools.GetTrace, stale, context.CancellationToken);
        Assert.AreEqual(expectedStatus, status, error);
        Assert.Contains(expectedError, error);
        Assert.Contains("cursor.stale", error, "The rejection names the stale cursor.");

        var seen = await ReadTraceSpanIdsAsync(path, input);
        Assert.AreSequenceEqual([root.SpanId, firstChild.SpanId, secondChild.SpanId, late.SpanId], seen,
            SequenceOrder.InAnyOrder, $"A fresh query returned {string.Join(", ", seen)}.");
    }

    private static OtlpIncidents EnvironmentIncidents(string service)
    {
        var incidents = new OtlpIncidents();
        incidents.Span($"{service}-failure", service, "checkout", s_now - 40 * Minute, error: true,
            attributes: new Dictionary<string, object> { ["error.type"] = "env-failure" }, resource: Environment("prod"));
        for (var i = 0; i < 150; i++)
            incidents.Span($"{service}-staging-{i}", service, "checkout", s_now - 30 * Minute + (ulong)i * Millisecond,
                resource: Environment("staging"));
        return incidents;
    }

    private static OtlpIncidents CohortIncidents(string service)
    {
        var incidents = new OtlpIncidents();
        foreach (var (region, start) in new[] { ("eu", s_now - 40 * Minute), ("us", s_now - 20 * Minute) })
            for (var i = 0; i < 10; i++)
            {
                incidents.Span($"{service}-{region}-error-{i}", service, "work", start + (ulong)i * Millisecond, error: true,
                    attributes: new Dictionary<string, object> { ["error.type"] = "cohort" },
                    resource: new Dictionary<string, object> { ["cloud.region"] = region });
                incidents.Span($"{service}-{region}-ok-{i}", service, "work", start + (ulong)(i + 100) * Millisecond,
                    resource: new Dictionary<string, object> { ["cloud.region"] = region });
            }
        return incidents;
    }

    private static Gauge QueueGauge(ulong start)
    {
        var gauge = new Gauge();
        foreach (var (shard, seconds, value) in new[] { ("b", 10UL, 3d), ("a", 40UL, 7d), ("a", 70UL, 4d), ("b", 100UL, 9d) })
            gauge.DataPoints.Add(new NumberDataPoint
            {
                Attributes = { OtlpEvidence.Pair("shard", shard) }, TimeUnixNano = start + seconds * Second, AsDouble = value
            });
        return gauge;
    }

    private async Task<IReadOnlyList<string>> ReadTraceSpanIdsAsync(AnalysisPath path, JsonObject input)
    {
        List<string> spans = [];
        HashSet<string> cursors = new(StringComparer.Ordinal);
        var next = input.DeepClone().AsObject();
        while (true)
        {
            var page = await Session.AskAsync<JsonElement>(path, AnalysisTools.GetTrace, next, context.CancellationToken);
            spans.AddRange(SpanIds(page));
            if (!page.GetProperty("has_more").GetBoolean()) return spans;
            var cursor = page.GetProperty("next_cursor").GetString()
                ?? throw new InvalidDataException("A nonfinal trace page has no cursor.");
            if (!cursors.Add(cursor)) throw new InvalidDataException("Trace paging repeated a cursor.");
            next["cursor"] = cursor;
        }
    }

    private static IEnumerable<string> SpanIds(JsonElement page) =>
        page.GetProperty("trace").GetProperty("spans").EnumerateArray().Select(span => span.GetProperty("span_id").GetString()!);

    private static Dictionary<string, object> Environment(string name) => new() { ["deployment.environment.name"] = name };

    private static JsonArray ScopeMatchers(string service, string environment) =>
        [AnswerKey.Matcher("service.name", "eq", service), AnswerKey.Matcher("deployment.environment.name", "eq", environment)];
}
