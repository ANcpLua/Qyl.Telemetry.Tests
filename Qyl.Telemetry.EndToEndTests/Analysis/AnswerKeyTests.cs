using System.Text.Json.Nodes;
using Qyl.Api.Contracts.Mcp.Tools;
using static Qyl.Telemetry.EndToEndTests.Analysis.OtlpIncidents;

namespace Qyl.Telemetry.EndToEndTests.Analysis;

/// <summary>Answers the analysis tools must give for the seeded incidents, through HTTP and through MCP.</summary>
[TestClass]
[DoNotParallelize]
public sealed class AnswerKeyTests(TestContext context)
{
    private static readonly SharedAnalysisSession s_shared = new();
    private static AnswerKey? s_key;

    private static AnalysisSession Session => s_shared.Session;
    private static AnswerKey Key => s_key ?? throw new InvalidOperationException("The answer key was not seeded.");

    [ClassInitialize]
    [Timeout(300_000, CooperativeCancellation = true)]
    public static async Task SeedAsync(TestContext context)
    {
        var session = await s_shared.StartAsync(context);
        s_key = new AnswerKey(session.TimeProvider.GetUtcNow());
        await s_key.Incidents.SendAndWaitAsync(session.Client, "db-root", context.CancellationToken);
    }

    [ClassCleanup]
    public static Task StopAsync() => s_shared.StopAsync();

    [TestMethod]
    [DataRow(AnalysisPath.Http)]
    [DataRow(AnalysisPath.Mcp)]
    public async Task Triage_SeededIncidents_RanksReleaseFailureFirstAndKeepsRareSilentAndSessionFindings(AnalysisPath path)
    {
        var triage = await Session.AskAsync<TriageOutput>(path, AnalysisTools.Triage, Key.Window(("matchers", new JsonArray(AnswerKey.Matcher("service.name", "in",
            new JsonArray("checkout", "orders-db", "payments", "billing", "sessions"))))), context.CancellationToken);
        var checkout = Assert.ContainsSingle(await CheckoutFailuresAsync(path));
        var first = triage.Failures[0];
        Assert.AreEqual(checkout.FailureId, first.FailureId);
        Assert.AreEqual(AnswerKey.Release, first.CoincidingRelease?.Version);
        Assert.Contains(failure => failure is { Count: 2, Service: "payments" }, triage.Failures);
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
    public async Task FailureIds_OneFailingExecution_AreTwoAndStable(AnalysisPath path)
    {
        var input = new JsonObject
        {
            ["start_time"] = AnswerKey.Iso(Key.T + Minute - 10 * Millisecond),
            ["end_time"] = AnswerKey.Iso(Key.T + Minute + 500 * Millisecond)
        };
        var first = await Session.AskAsync<TriageOutput>(path, AnalysisTools.Triage, input, context.CancellationToken);
        var second = await Session.AskAsync<TriageOutput>(path, AnalysisTools.Triage, input, context.CancellationToken);
        Assert.HasCount(2, first.Failures);
        Assert.AreSequenceEqual(first.Failures.Select(failure => failure.FailureId), second.Failures.Select(failure => failure.FailureId));
    }

    [TestMethod]
    [DataRow(AnalysisPath.Http)]
    [DataRow(AnalysisPath.Mcp)]
    public async Task FailureContext_DatabaseTimeoutIncident_NamesTheTimeoutAsRootCause(AnalysisPath path)
    {
        var failure = await Session.AskAsync<GetFailureContextOutput>(path, AnalysisTools.FailureContext, Key.Window(("trace_id", Key["db-root"].TraceId)), context.CancellationToken);
        Assert.AreEqual(Key["db-timeout"].SpanId, failure.Observed.RootCauseSpan?.SpanId);
    }

    [TestMethod]
    [DataRow(AnalysisPath.Http)]
    [DataRow(AnalysisPath.Mcp)]
    public async Task FailureContext_NewArrival_IsUnsettled(AnalysisPath path)
    {
        var incidents = new OtlpIncidents();
        var arrival = incidents.Span($"arrival-{path}-{Guid.NewGuid():N}", "arrival-test", "arrival",
            Nanoseconds(Session.TimeProvider.GetUtcNow()) - Millisecond, 1, error: true);
        await incidents.SendAsync(Session.Client, context.CancellationToken);
        var output = await Session.AskAsync<GetFailureContextOutput>(path, AnalysisTools.FailureContext,
            new JsonObject { ["trace_id"] = arrival.TraceId, ["lookback_minutes"] = 60 }, context.CancellationToken);
        Assert.IsFalse(output.Settled);
    }

    [TestMethod]
    [DataRow(AnalysisPath.Http)]
    [DataRow(AnalysisPath.Mcp)]
    public async Task CompareGroups_ErrorGroup_DiscriminatesByRegionAndRelease(AnalysisPath path)
    {
        var output = await Session.AskAsync<CompareGroupsOutput>(path, AnalysisTools.CompareGroups, Key.Window(
            ("matchers", new JsonArray(AnswerKey.Matcher("service.name", "eq", "regional"))), ("signal", "spans"),
            ("group_a", new JsonObject { ["matchers"] = new JsonArray(new JsonObject { ["key"] = "error.type", ["op"] = "exists" }) }),
            ("group_b", "rest"), ("attribute_scope", "both")), context.CancellationToken);
        Assert.AreSequenceEqual(["cloud.region=eu", "service.version=2.3"],
            output.Discriminators.Take(2).Select(association => $"{association.Key}={association.Value}"), SequenceOrder.InAnyOrder);
    }

    [TestMethod]
    [DataRow(AnalysisPath.Http)]
    [DataRow(AnalysisPath.Mcp)]
    public async Task VerifyFix_FourRequestsAfterFix_IsPendingForNinetySixMore(AnalysisPath path)
    {
        var checkout = Assert.ContainsSingle(await CheckoutFailuresAsync(path));
        var verification = await Session.AskAsync<VerifyFixOutput>(path, AnalysisTools.VerifyFix,
            Key.Window(("failure_id", checkout.FailureId), ("since", AnswerKey.Iso(Key.T2))), context.CancellationToken);
        Assert.AreEqual("pending", verification.Verdict);
        Assert.AreEqual(AnswerKey.FixRequestsNeeded, verification.Needs?.Requests);
    }

    [TestMethod]
    [DataRow(AnalysisPath.Http)]
    [DataRow(AnalysisPath.Mcp)]
    public async Task VerifyFix_InsufficientEvidence_NeverVerifies(AnalysisPath path)
    {
        var checkout = Assert.ContainsSingle(await CheckoutFailuresAsync(path));
        var since = AnswerKey.Iso(Key.Now - Minute);
        var waiting = await Session.AskAsync<VerifyFixOutput>(path, AnalysisTools.VerifyFix, Key.Window(("failure_id", checkout.FailureId), ("since", since)), context.CancellationToken);
        Assert.AreEqual("pending", waiting.Verdict);
        Assert.AreEqual(0L, waiting.After.Requests);
        var unobserved = await Session.AskAsync<VerifyFixOutput>(path, AnalysisTools.VerifyFix, Key.Window(("failure_id", checkout.FailureId), ("since", since),
            ("min_requests", 0), ("min_minutes", 0)), context.CancellationToken);
        Assert.AreEqual("inconclusive", unobserved.Verdict, "Zero requests after the change cannot pass, even without thresholds.");
        var recurring = await Session.AskAsync<VerifyFixOutput>(path, AnalysisTools.VerifyFix, Key.Window(("failure_id", checkout.FailureId),
            ("since", AnswerKey.Iso(Key.T - Minute)), ("min_requests", 0), ("min_minutes", 0)), context.CancellationToken);
        Assert.AreEqual("failed", recurring.Verdict);
        Assert.Contains(Key["db-root"].TraceId, recurring.Recurrences);
    }

    [TestMethod]
    [DataRow(AnalysisPath.Http)]
    [DataRow(AnalysisPath.Mcp)]
    public async Task QueryMetric_HistogramP95_StaysInSecondsAndIsApproximate(AnalysisPath path)
    {
        var output = await Session.AskAsync<QueryMetricOutput>(path, AnalysisTools.QueryMetric, Key.Window(("metric_name", "http.server.request.duration"),
            ("aggregation", "p95"), ("group_by", new JsonArray("http.route")), ("step_ms", 3_600_000)), context.CancellationToken);
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
    public async Task QueryMetric_CumulativeCounterIncrease_IsExact(AnalysisPath path)
    {
        var output = await Session.AskAsync<QueryMetricOutput>(path, AnalysisTools.QueryMetric, Key.Window(("metric_name", "requests.total"),
            ("function", "increase"), ("step_ms", 3_600_000)), context.CancellationToken);
        Assert.IsNotNull(output.Series);
        Assert.AreEqual(AnswerKey.CounterIncrease, output.Series.SelectMany(series => series.Buckets).Sum(bucket => bucket.Value ?? 0));
    }

    private async Task<IEnumerable<TriageFailure>> CheckoutFailuresAsync(AnalysisPath path) =>
        (await Session.AskAsync<TriageOutput>(path, AnalysisTools.Triage, Key.Window(("service_name", "checkout"), ("status", "all")), context.CancellationToken))
            .Failures.Where(failure => failure.Operation == "checkout");

    private static HashSet<string> TraceIds(string prefix) => Key.Incidents.Keys.Where(pair => pair.Key.StartsWith(prefix, StringComparison.Ordinal))
        .Select(pair => pair.Value.TraceId).ToHashSet(StringComparer.Ordinal);
}
