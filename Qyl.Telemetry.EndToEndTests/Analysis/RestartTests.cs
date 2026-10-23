using System.Text.Json;
using System.Text.Json.Nodes;
using Qyl.Api.Contracts.Common;
using Qyl.Api.Contracts.Mcp.Tools;
using static Qyl.Telemetry.EndToEndTests.Analysis.OtlpIncidents;
using StoredTrace = Qyl.Api.Contracts.OTel.Traces.Trace;

namespace Qyl.Telemetry.EndToEndTests.Analysis;

/// <summary>Exact values, identities and analysis cursors survive ingestion and a restart on the same data path.</summary>
[TestClass]
[DoNotParallelize]
public sealed class RestartTests(TestContext context)
{
    private const string Service = "durable";
    private const string RootKey = "durable-root";
    private const int FailureKinds = 5;
    private static readonly string[] s_exactStrings = ["a", "", "a"];
    private AnalysisSession? _session;

    private AnalysisSession Session => _session ?? throw new InvalidOperationException("The Collector has not started.");

    [TestInitialize]
    public async Task StartAsync() => _session = await AnalysisSession.StartAsync(context);

    [TestCleanup]
    public async Task StopAsync()
    {
        if (_session is not null) await _session.DisposeAsync();
    }

    [TestMethod]
    [DataRow(AnalysisPath.Http)]
    [DataRow(AnalysisPath.Mcp)]
    [Timeout(180_000, CooperativeCancellation = true)]
    public async Task Collector_Restart_KeepsExactValuesIdsAndAFailureCursor(AnalysisPath path)
    {
        var now = Nanoseconds(Session.TimeProvider.GetUtcNow());
        var incidents = DurableIncidents(now);
        var root = incidents.Keys[RootKey];
        await incidents.SendAndWaitAsync(Session.Client, RootKey, context.CancellationToken);
        var window = new JsonObject
        {
            ["start_time"] = AnswerKey.Iso(now - 30 * Minute), ["end_time"] = AnswerKey.Iso(now + Minute), ["service_name"] = Service
        };

        var before = await Session.Client.ReadTraceAsync(root.TraceId, context.CancellationToken);
        var all = await FailuresAsync(path, window, null);
        Assert.HasCount(FailureKinds, all.Failures);
        var firstPage = await FailuresAsync(path, window, 2);
        Assert.IsTrue(firstPage.HasMore);
        Assert.IsNotNull(firstPage.NextCursor);

        await Session.RestartAsync(context.CancellationToken);

        var after = await Session.Client.ReadTraceAsync(root.TraceId, context.CancellationToken);
        Assert.AreEqual(before.GetRawText(), after.GetRawText(), "The stored trace changed across the restart.");
        var stored = after.Deserialize<StoredTrace>(JsonSerializerOptions.Web);
        Assert.IsNotNull(stored);
        var attributes = Assert.ContainsSingle(stored.Spans.Where(span => span.SpanId == root.SpanId)).Attributes!
            .ToDictionary(attribute => attribute.Key, attribute => attribute.Value, StringComparer.Ordinal);
        Assert.AreEqual(long.MaxValue, Assert.IsInstanceOfType<AttributeIntValue>(
            Assert.IsInstanceOfType<AttributeValue.ObjectValue>(attributes["exact.int"]).Value).Value);
        Assert.AreEqual(.1 + .2, Assert.IsInstanceOfType<AttributeDoubleValue>(
            Assert.IsInstanceOfType<AttributeValue.ObjectValue>(attributes["exact.double"]).Value).Value);
        Assert.IsTrue(Assert.IsInstanceOfType<AttributeValue.BoolValue>(attributes["exact.bool"]).Value);
        Assert.AreSequenceEqual(s_exactStrings, Assert.IsInstanceOfType<AttributeValue.ArrayValue>(attributes["exact.strings"]).Value
            .Select(value => Assert.IsInstanceOfType<AttributeValue.StringValue>(value).Value));

        var restarted = await FailuresAsync(path, window, null);
        Assert.AreSequenceEqual(all.Failures.Select(failure => failure.FailureId), restarted.Failures.Select(failure => failure.FailureId));
        var secondPage = await FailuresAsync(path, window, 2, firstPage.NextCursor);
        Assert.IsTrue(secondPage.HasMore);
        Assert.IsNotNull(secondPage.NextCursor);
        var lastPage = await FailuresAsync(path, window, 2, secondPage.NextCursor);
        Assert.IsFalse(lastPage.HasMore);
        Assert.IsNull(lastPage.NextCursor);
        var paged = firstPage.Failures.Concat(secondPage.Failures).Concat(lastPage.Failures)
            .Select(failure => failure.FailureId);
        Assert.AreSequenceEqual(all.Failures.Select(failure => failure.FailureId), paged,
            "A cursor issued before the restart must continue the same ordered failure list.");
    }

    private static OtlpIncidents DurableIncidents(ulong now)
    {
        var incidents = new OtlpIncidents();
        var root = incidents.Span(RootKey, Service, "durable", now - 10 * Minute, 50, attributes: new Dictionary<string, object>
        {
            ["exact.int"] = long.MaxValue, ["exact.double"] = .1 + .2, ["exact.bool"] = true, ["exact.strings"] = s_exactStrings
        });
        for (var i = 0; i < FailureKinds; i++)
            incidents.Span($"durable-failure-{i}", Service, $"operation-{i}", now - (9 - (ulong)i) * Minute, 5, error: true,
                parent: root.SpanId, trace: root.TraceId, attributes: new Dictionary<string, object> { ["error.type"] = $"kind-{i}" },
                message: $"failure {i}");
        return incidents;
    }

    private Task<TriageOutput> FailuresAsync(AnalysisPath path, JsonObject window, int? limit, string? cursor = null)
    {
        var input = window.DeepClone().AsObject();
        input["status"] = "all";
        if (limit is { } size) input["limit"] = size;
        if (cursor is not null) input["cursor"] = cursor;
        return Session.AskAsync<TriageOutput>(path, AnalysisTools.Triage, input, context.CancellationToken);
    }
}
