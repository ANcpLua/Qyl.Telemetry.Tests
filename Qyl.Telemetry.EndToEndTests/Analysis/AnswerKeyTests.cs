using System.Text.Json.Nodes;
using Qyl.Api.Contracts.Mcp.Tools;
using static Qyl.Telemetry.EndToEndTests.Analysis.OtlpIncidents;

namespace Qyl.Telemetry.EndToEndTests.Analysis;

/// <summary>Answers the analysis tools must give for the seeded incidents, through HTTP and through MCP.</summary>
[TestClass]
[DoNotParallelize]
public sealed class AnswerKeyTests(TestContext context)
{
    private static AnalysisSession? _session;
    private static AnswerKey? _key;

    private static AnalysisSession Session => _session ?? throw new InvalidOperationException("The Collector has not started.");
    private static AnswerKey Key => _key ?? throw new InvalidOperationException("The answer key was not seeded.");

    [ClassInitialize]
    [Timeout(300_000, CooperativeCancellation = true)]
    public static async Task SeedAsync(TestContext context)
    {
        _session = await AnalysisSession.StartAsync(context);
        _key = new AnswerKey(DateTimeOffset.UtcNow);
        await _key.Incidents.SendAsync(_session.Client, context.CancellationToken);
        _ = await _session.Client.WaitForTraceAsync(_key["db-root"].TraceId,
            trace => trace.GetProperty("spans").GetArrayLength() == 7, context.CancellationToken);
    }

    [ClassCleanup]
    public static async Task StopAsync()
    {
        if (_session is not null) await _session.DisposeAsync();
    }

    [TestMethod]
    [DataRow(AnalysisPath.Http)]
    [DataRow(AnalysisPath.Mcp)]
    public async Task Triage_ranks_the_release_failure_first_and_keeps_rare_silent_and_session_findings(AnalysisPath path)
    {
        var triage = await AskAsync<TriageOutput>(path, "triage", Input(("matchers", new JsonArray(Matcher("service.name", "in",
            new JsonArray("checkout", "orders-db", "payments", "billing", "sessions"))))));
        var checkout = await CheckoutFailureAsync(path);
        var first = triage.Failures[0];
        Assert.AreEqual(checkout.FailureId, first.FailureId);
        Assert.AreEqual(AnswerKey.Release, first.CoincidingRelease?.Version);
        Assert.Contains(failure => failure.Count == 2 && failure.Service == "payments", triage.Failures);
        Assert.Contains(service => service.Service == "billing", triage.SilentServices);
        var session = Assert.ContainsSingle(triage.Failures.Where(failure => failure.Service == "sessions"));
        Assert.AreEqual(3L, session.Count, "Only sess_bad contributes failures.");
        Assert.Contains(session.ExampleTraceId!, TraceIds("sess_bad-"));
        Assert.DoesNotContain(failure => TraceIds("sess_ok-").Contains(failure.ExampleTraceId!), triage.Failures);
        Assert.DoesNotContain(regression => regression.Service == "sessions", triage.Regressions);
    }

    [TestMethod]
    [DataRow(AnalysisPath.Http)]
    [DataRow(AnalysisPath.Mcp)]
    public async Task Failure_ids_of_one_failing_execution_are_two_and_stable(AnalysisPath path)
    {
        var input = new JsonObject
        {
            ["start_time"] = AnswerKey.Iso(Key.T + Minute - 10 * Millisecond),
            ["end_time"] = AnswerKey.Iso(Key.T + Minute + 500 * Millisecond)
        };
        var first = await AskAsync<TriageOutput>(path, "triage", input);
        var second = await AskAsync<TriageOutput>(path, "triage", input);
        Assert.HasCount(2, first.Failures);
        Assert.AreSequenceEqual(first.Failures.Select(failure => failure.FailureId), second.Failures.Select(failure => failure.FailureId));
    }

    [TestMethod]
    [DataRow(AnalysisPath.Http)]
    [DataRow(AnalysisPath.Mcp)]
    public async Task Failure_context_names_the_database_timeout_as_root_cause(AnalysisPath path)
    {
        var failure = await AskAsync<GetFailureContextOutput>(path, "get_failure_context", Input(("trace_id", Key["db-root"].TraceId)));
        Assert.AreEqual(Key["db-timeout"].SpanId, failure.Observed.RootCauseSpan?.SpanId);
    }

    [TestMethod]
    [DataRow(AnalysisPath.Http)]
    [DataRow(AnalysisPath.Mcp)]
    public async Task Failure_context_of_a_new_arrival_is_unsettled(AnalysisPath path)
    {
        var incidents = new OtlpIncidents();
        var arrival = incidents.Span($"arrival-{path}-{Guid.NewGuid():N}", "arrival-test", "arrival",
            Nanoseconds(DateTimeOffset.UtcNow) - Millisecond, 1, error: true);
        await incidents.SendAsync(Session.Client, context.CancellationToken);
        var output = await AskAsync<GetFailureContextOutput>(path, "get_failure_context",
            new JsonObject { ["trace_id"] = arrival.TraceId, ["lookback_minutes"] = 60 });
        Assert.IsFalse(output.Settled);
    }

    [TestMethod]
    [DataRow(AnalysisPath.Http)]
    [DataRow(AnalysisPath.Mcp)]
    public async Task Error_group_discriminators_are_the_region_and_release(AnalysisPath path)
    {
        var output = await AskAsync<CompareGroupsOutput>(path, "compare_groups", Input(
            ("matchers", new JsonArray(Matcher("service.name", "eq", "regional"))), ("signal", "spans"),
            ("group_a", new JsonObject { ["matchers"] = new JsonArray(new JsonObject { ["key"] = "error.type", ["op"] = "exists" }) }),
            ("group_b", "rest"), ("attribute_scope", "both")));
        Assert.AreEquivalent(["cloud.region=eu", "service.version=2.3"],
            output.Discriminators.Take(2).Select(association => $"{association.Key}={association.Value}"));
    }

    [TestMethod]
    [DataRow(AnalysisPath.Http)]
    [DataRow(AnalysisPath.Mcp)]
    public async Task Four_requests_after_a_fix_leave_it_pending_for_ninety_six_more(AnalysisPath path)
    {
        var checkout = await CheckoutFailureAsync(path);
        var verification = await AskAsync<VerifyFixOutput>(path, "verify_fix",
            Input(("failure_id", checkout.FailureId), ("since", AnswerKey.Iso(Key.T2))));
        Assert.AreEqual("pending", verification.Verdict);
        Assert.AreEqual(AnswerKey.FixRequestsNeeded, verification.Needs?.Requests);
    }

    [TestMethod]
    [DataRow(AnalysisPath.Http)]
    [DataRow(AnalysisPath.Mcp)]
    public async Task Insufficient_evidence_never_verifies_a_fix(AnalysisPath path)
    {
        var checkout = await CheckoutFailureAsync(path);
        var since = AnswerKey.Iso(Key.Now - Minute);
        var waiting = await AskAsync<VerifyFixOutput>(path, "verify_fix", Input(("failure_id", checkout.FailureId), ("since", since)));
        Assert.AreEqual("pending", waiting.Verdict);
        Assert.AreEqual(0L, waiting.After.Requests);
        var unobserved = await AskAsync<VerifyFixOutput>(path, "verify_fix", Input(("failure_id", checkout.FailureId), ("since", since),
            ("min_requests", 0), ("min_minutes", 0)));
        Assert.AreEqual("inconclusive", unobserved.Verdict, "Zero requests after the change cannot pass, even without thresholds.");
        var recurring = await AskAsync<VerifyFixOutput>(path, "verify_fix", Input(("failure_id", checkout.FailureId),
            ("since", AnswerKey.Iso(Key.T - Minute)), ("min_requests", 0), ("min_minutes", 0)));
        Assert.AreEqual("failed", recurring.Verdict);
        Assert.Contains(Key["db-root"].TraceId, recurring.Recurrences);
    }

    [TestMethod]
    [DataRow(AnalysisPath.Http)]
    [DataRow(AnalysisPath.Mcp)]
    public async Task Histogram_p95_stays_in_seconds_and_is_marked_approximate(AnalysisPath path)
    {
        var output = await AskAsync<QueryMetricOutput>(path, "query_metric", Input(("metric_name", "http.server.request.duration"),
            ("aggregation", "p95"), ("group_by", new JsonArray("http.route")), ("step_ms", 3_600_000)));
        Assert.AreEqual("s", output.Unit);
        Assert.IsTrue(output.Approximate);
        Assert.IsNotNull(output.Series);
        var checkout = Assert.ContainsSingle(output.Series.Where(series => series.Attributes.Any(attribute =>
            attribute is { Key: "http.route", Value: Qyl.Api.Contracts.Common.AttributeValue.StringValue { Value: "/checkout" } })));
        Assert.Contains(value => Math.Abs(value - AnswerKey.CheckoutSeconds) < .1,
            checkout.Buckets.Select(bucket => bucket.Value).OfType<double>());
    }

    [TestMethod]
    [DataRow(AnalysisPath.Http)]
    [DataRow(AnalysisPath.Mcp)]
    public async Task Cumulative_counter_increase_is_exact(AnalysisPath path)
    {
        var output = await AskAsync<QueryMetricOutput>(path, "query_metric", Input(("metric_name", "requests.total"),
            ("function", "increase"), ("step_ms", 3_600_000)));
        Assert.IsNotNull(output.Series);
        Assert.AreEqual(AnswerKey.CounterIncrease, output.Series.SelectMany(series => series.Buckets).Sum(bucket => bucket.Value ?? 0));
    }

    private Task<T> AskAsync<T>(AnalysisPath path, string tool, JsonObject input) =>
        Session.AskAsync<T>(path, tool, input, context.CancellationToken);

    private async Task<TriageFailure> CheckoutFailureAsync(AnalysisPath path) =>
        Assert.ContainsSingle((await AskAsync<TriageOutput>(path, "triage", Input(("service_name", "checkout"), ("status", "all"))))
            .Failures.Where(failure => failure.Operation == "checkout"));

    private static JsonObject Input(params (string Key, JsonNode? Value)[] fields)
    {
        var input = Key.Window();
        foreach (var (key, value) in fields) input[key] = value;
        return input;
    }

    private static JsonObject Matcher(string key, string op, JsonNode value) => op == "in"
        ? new JsonObject { ["key"] = key, ["op"] = op, ["values"] = value, ["scope"] = "resource" }
        : new JsonObject { ["key"] = key, ["op"] = op, ["value"] = value, ["scope"] = "resource" };

    private static HashSet<string> TraceIds(string prefix) => Key.Incidents.Keys.Where(pair => pair.Key.StartsWith(prefix, StringComparison.Ordinal))
        .Select(pair => pair.Value.TraceId).ToHashSet(StringComparer.Ordinal);
}
