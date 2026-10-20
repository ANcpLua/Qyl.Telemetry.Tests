using System.Text.Json;
using System.Text.Json.Nodes;
using OpenTelemetry.Proto.Metrics.V1;
using Qyl.Api.Contracts.Mcp.Tools;
using static Qyl.Telemetry.EndToEndTests.Analysis.OtlpIncidents;

namespace Qyl.Telemetry.EndToEndTests.Analysis;

/// <summary>Independent incidents whose correct analysis answers the reported engine defects contradict.</summary>
[TestClass]
[DoNotParallelize]
public sealed class EngineDefectTests(TestContext context)
{
    private static AnalysisSession? _session;
    private static ulong _now;

    private static AnalysisSession Session => _session ?? throw new InvalidOperationException("The Collector has not started.");

    [ClassInitialize]
    [Timeout(300_000, CooperativeCancellation = true)]
    public static async Task StartAsync(TestContext context)
    {
        _session = await AnalysisSession.StartAsync(context);
        _now = Nanoseconds(DateTimeOffset.UtcNow);
    }

    [ClassCleanup]
    public static async Task StopAsync()
    {
        if (_session is not null) await _session.DisposeAsync();
    }

    [TestMethod]
    [DataRow(AnalysisPath.Http)]
    [DataRow(AnalysisPath.Mcp)]
    public async Task Verify_fix_counts_only_requests_of_the_selected_environment(AnalysisPath path)
    {
        var service = $"env-{path}".ToLowerInvariant();
        var incidents = new OtlpIncidents();
        incidents.Span($"{service}-failure", service, "checkout", _now - 40 * Minute, error: true,
            attributes: new Dictionary<string, object> { ["error.type"] = "env-failure" }, resource: Environment("prod"));
        for (var i = 0; i < 150; i++)
            incidents.Span($"{service}-staging-{i}", service, "checkout", _now - 30 * Minute + (ulong)i * Millisecond, resource: Environment("staging"));
        await SendAsync(incidents, $"{service}-staging-149");
        var failure = Assert.ContainsSingle((await AskAsync<TriageOutput>(path, "triage",
            Window(("service_name", service), ("environment", "prod"), ("status", "all")))).Failures);
        // Since 17.0.1 verify_fix scopes requests and failures by one set of matchers (or by a failure_id alone), so the
        // environment is part of the matchers rather than a separate argument.
        Assert.IsNotNull(failure.FailureId);
        var verification = await AskAsync<VerifyFixOutput>(path, "verify_fix", Window(("matchers", ScopeMatchers(service, "prod")),
            ("since", AnswerKey.Iso(_now - 35 * Minute)), ("min_minutes", 1)));
        Assert.AreEqual(0L, verification.After.Requests, "Staging requests must not count for prod.");
        Assert.AreEqual("pending", verification.Verdict);
    }

    [TestMethod]
    [DataRow(AnalysisPath.Http)]
    [DataRow(AnalysisPath.Mcp)]
    public async Task Failure_context_of_a_session_keeps_a_span_id_reused_across_its_traces(AnalysisPath path)
    {
        var service = $"reuse-{path}".ToLowerInvariant();
        var incidents = new OtlpIncidents();
        var first = incidents.Span($"{service}-a", service, "first", _now - 10 * Minute, error: true, session: service,
            attributes: new Dictionary<string, object> { ["error.type"] = "reuse" });
        var second = incidents.Span($"{service}-b", service, "second", _now - 9 * Minute, error: true, session: service, spanId: first.SpanId,
            attributes: new Dictionary<string, object> { ["error.type"] = "reuse" });
        await SendAsync(incidents, $"{service}-a");
        await SendAsync(incidents, $"{service}-b");
        var output = await AskAsync<GetFailureContextOutput>(path, "get_failure_context", Window(("session_id", service)));
        Assert.AreEqual(first.TraceId, output.Observed.RootCauseSpan?.TraceId);
        Assert.AreEquivalent(new[] { first.TraceId, second.TraceId },
            output.Observed.ErrorSpans.Select(span => span.TraceId).ToArray());
    }

    [TestMethod]
    [DataRow(AnalysisPath.Http)]
    [DataRow(AnalysisPath.Mcp)]
    public async Task Compare_groups_limits_a_failure_cohort_to_its_window(AnalysisPath path)
    {
        var service = $"cohort-{path}".ToLowerInvariant();
        var incidents = new OtlpIncidents();
        foreach (var (region, start) in new[] { ("eu", _now - 40 * Minute), ("us", _now - 20 * Minute) })
            for (var i = 0; i < 10; i++)
            {
                incidents.Span($"{service}-{region}-error-{i}", service, "work", start + (ulong)i * Millisecond, error: true,
                    attributes: new Dictionary<string, object> { ["error.type"] = "cohort" },
                    resource: new Dictionary<string, object> { ["cloud.region"] = region });
                incidents.Span($"{service}-{region}-ok-{i}", service, "work", start + (ulong)(i + 100) * Millisecond,
                    resource: new Dictionary<string, object> { ["cloud.region"] = region });
            }
        await SendAsync(incidents, $"{service}-us-ok-9");
        var failure = Assert.ContainsSingle((await AskAsync<TriageOutput>(path, "triage",
            Window(("service_name", service), ("status", "all")))).Failures);
        var output = await AskAsync<CompareGroupsOutput>(path, "compare_groups", Window(
            ("matchers", new JsonArray(new JsonObject { ["key"] = "service.name", ["op"] = "eq", ["value"] = service, ["scope"] = "resource" })),
            ("signal", "spans"), ("group_b", "rest"), ("attribute_scope", "both"),
            ("group_a", new JsonObject
            {
                ["failure_id"] = failure.FailureId,
                ["window"] = new JsonObject { ["start_time"] = AnswerKey.Iso(_now - 45 * Minute), ["end_time"] = AnswerKey.Iso(_now - 30 * Minute) }
            })));
        Assert.AreEqual(10L, output.SizeA, "Only the occurrences inside the cohort window belong to it.");
        var cohortRegion = Assert.ContainsSingle(output.Discriminators.Where(association => association.Key == "cloud.region"));
        Assert.AreEqual("eu", cohortRegion.Value.ToString());
        Assert.AreEqual(1d, cohortRegion.ShareA);
    }

    [TestMethod]
    [DataRow(AnalysisPath.Http)]
    [DataRow(AnalysisPath.Mcp)]
    public async Task Last_takes_the_latest_point_of_each_bucket_across_series(AnalysisPath path)
    {
        var name = $"queue.depth.{path}".ToLowerInvariant();
        var start = Nanoseconds(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.AddMinutes(-10).ToUnixTimeSeconds() / 60 * 60));
        var gauge = new Gauge();
        foreach (var (shard, seconds, value) in new[] { ("b", 10UL, 3d), ("a", 40UL, 7d), ("a", 70UL, 4d), ("b", 100UL, 9d) })
            gauge.DataPoints.Add(new NumberDataPoint
            {
                Attributes = { Pair("shard", shard) }, TimeUnixNano = start + seconds * 1000 * Millisecond, AsDouble = value
            });
        var incidents = new OtlpIncidents();
        incidents.Metric("metric-last", new Metric { Name = name, Unit = "{item}", Gauge = gauge });
        await incidents.SendAsync(Session.Client, context.CancellationToken);
        var output = await AskAsync<QueryMetricOutput>(path, "query_metric", new JsonObject
        {
            ["metric_name"] = name, ["aggregation"] = "last", ["step_ms"] = 60_000,
            ["start_time"] = AnswerKey.Iso(start), ["end_time"] = AnswerKey.Iso(start + 2 * Minute)
        });
        Assert.IsNotNull(output.Series);
        Assert.AreSequenceEqual([7d, 9d], Assert.ContainsSingle(output.Series).Buckets.Select(bucket => bucket.Value ?? double.NaN));
    }

    [TestMethod]
    [DataRow(AnalysisPath.Http)]
    [DataRow(AnalysisPath.Mcp)]
    public async Task Histogram_percentiles_stay_within_negative_bounds(AnalysisPath path)
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
                        StartTimeUnixNano = _now - 11 * Minute, TimeUnixNano = _now - 10 * Minute, Count = 100, Sum = -1600,
                        Min = -20, Max = -12, ExplicitBounds = { -10, -5, 0 }, BucketCounts = { 100, 0, 0, 0 }
                    }
                }
            }
        });
        await incidents.SendAsync(Session.Client, context.CancellationToken);
        var output = await AskAsync<QueryMetricOutput>(path, "query_metric", Window(("metric_name", name), ("aggregation", "p50"),
            ("step_ms", 3_600_000)));
        Assert.IsNotNull(output.Series);
        var median = Assert.ContainsSingle(Assert.ContainsSingle(output.Series).Buckets.Select(bucket => bucket.Value).OfType<double>());
        Assert.IsInRange(-20d, -12d, median);
    }

    [TestMethod]
    [DataRow(AnalysisPath.Http)]
    [DataRow(AnalysisPath.Mcp)]
    public async Task Trace_paging_rejects_a_stale_cursor_and_a_fresh_query_includes_the_late_span(AnalysisPath path)
    {
        var service = $"paging-{path}".ToLowerInvariant();
        var start = _now - 5 * Minute;
        var incidents = new OtlpIncidents();
        var root = incidents.Span($"{service}-root", service, "root", start + 10_000 * Millisecond, 30_000);
        foreach (var seconds in new ulong[] { 20, 30 })
            incidents.Span($"{service}-child-{seconds}", service, "child", start + seconds * 1000 * Millisecond, 1,
                parent: root.SpanId, trace: root.TraceId);
        await SendAsync(incidents, $"{service}-child-30");
        var input = Window(("trace_id", root.TraceId), ("max_spans", 2));
        var page = await AskAsync<JsonElement>(path, "get_trace", input);
        Assert.IsTrue(page.GetProperty("has_more").GetBoolean(), "Two of three spans fill the first page.");
        var late = incidents.Span($"{service}-late", service, "late", start + 5_000 * Millisecond, 1, parent: root.SpanId, trace: root.TraceId);
        await SendAsync(incidents, $"{service}-late");

        // A span that sorts ahead of the issued cursor invalidates it: since qyl 17.0.3 the continuation is refused with cursor.stale.
        var stale = input.DeepClone().AsObject();
        stale["cursor"] = page.GetProperty("next_cursor").GetString();
        var (status, error) = await Session.AskErrorAsync(path, "get_trace", stale, context.CancellationToken);
        if (path == AnalysisPath.Http) Assert.AreEqual(400, status, error);
        else Assert.Contains("collector_rejected", error, "The MCP tool error carries the Collector's rejection.");
        Assert.Contains("cursor.stale", error, "The rejection names the stale cursor.");

        var seen = new List<string>();
        page = await AskAsync<JsonElement>(path, "get_trace", input);
        seen.AddRange(SpanIds(page));
        while (page.GetProperty("has_more").GetBoolean())
        {
            var next = input.DeepClone().AsObject();
            next["cursor"] = page.GetProperty("next_cursor").GetString();
            page = await AskAsync<JsonElement>(path, "get_trace", next);
            seen.AddRange(SpanIds(page));
        }
        string[] expected = [root.SpanId, incidents.Keys[$"{service}-child-20"].SpanId, incidents.Keys[$"{service}-child-30"].SpanId, late.SpanId];
        Assert.AreSequenceEqual([.. expected.Order()], [.. seen.Order()], $"A fresh query returned {string.Join(", ", seen)}.");
    }

    private static IEnumerable<string> SpanIds(JsonElement page) =>
        page.GetProperty("trace").GetProperty("spans").EnumerateArray().Select(span => span.GetProperty("span_id").GetString()!);

    private async Task SendAsync(OtlpIncidents incidents, string lastKey)
    {
        var last = incidents.Keys[lastKey];
        var expected = incidents.Keys.Values.Count(key => key.TraceId == last.TraceId);
        await incidents.SendAsync(Session.Client, context.CancellationToken);
        _ = await Session.Client.WaitForTraceAsync(last.TraceId, trace => trace.GetProperty("spans").GetArrayLength() >= expected,
            context.CancellationToken);
    }

    private Task<T> AskAsync<T>(AnalysisPath path, string tool, JsonObject input) =>
        Session.AskAsync<T>(path, tool, input, context.CancellationToken);

    private static JsonObject Window(params (string Key, JsonNode? Value)[] fields)
    {
        var input = new JsonObject { ["start_time"] = AnswerKey.Iso(_now - 60 * Minute), ["end_time"] = AnswerKey.Iso(_now + Minute) };
        foreach (var (key, value) in fields) input[key] = value;
        return input;
    }

    private static Dictionary<string, object> Environment(string name) => new() { ["deployment.environment.name"] = name };

    private static JsonArray ScopeMatchers(string service, string environment) =>
        [new JsonObject { ["key"] = "service.name", ["op"] = "eq", ["value"] = service, ["scope"] = "resource" },
         new JsonObject { ["key"] = "deployment.environment.name", ["op"] = "eq", ["value"] = environment, ["scope"] = "resource" }];
}
