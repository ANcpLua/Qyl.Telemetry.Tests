using System.Diagnostics;
using Google.Protobuf;
using JetBrains.Annotations;
using Qyl.Telemetry.SemanticConventions;
using static Qyl.Telemetry.Tests.Evidence.OtlpEvidence;

namespace Qyl.Telemetry.IntegrationTests.Receiver;

[TestClass]
public sealed class CaptureLifecycleTests
{
    [UsedImplicitly] public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task Concurrent_capture_invocations_have_distinct_roots_and_keep_unrelated_wire_records_visible()
    {
        var previous = Activity.Current;
        var results = await Task.WhenAll(CaptureAsync(1), CaptureAsync(2));
        Assert.AreNotEqual(results[0].TraceId, results[1].TraceId);
        Assert.AreSame(previous, Activity.Current);
        for (var index = 0; index < results.Length; index++)
        {
            var result = results[index];
            var span = Assert.ContainsSingle(result.Spans);
            Assert.AreEqual(result.TraceId, span.TraceId);
            Assert.AreEqual("capture." + (index + 1), span.Name);
            Assert.AreEqual(SpanKind.Consumer, span.Kind);
            Assert.AreEqual(ActivityStatusCode.Error, span.Status);
            Assert.AreEqual("rich-scope", span.Scope?.Name);
            Assert.HasCount(2, result.Capture.Spans);
        }
    }

    [TestMethod]
    public async Task Multiple_waiters_are_notified_by_the_same_real_export()
    {
        await using var receiver = await OtlpReceiver.StartAsync(TestContext.CancellationToken);
        var first = receiver.AwaitAsync(s => s.Spans.Count == 1, TimeSpan.FromSeconds(10),
            TestContext.CancellationToken);
        var second = receiver.AwaitAsync(s => s.Receipts.Count == 1, TimeSpan.FromSeconds(10),
            TestContext.CancellationToken);
        using var response = await ReceiverTests.PostAsync(receiver.Endpoint, RichSpan().ToByteArray(),
            TestContext.CancellationToken);
        response.EnsureSuccessStatusCode();
        var results = await Task.WhenAll(first, second);
        foreach (var result in results)
        {
            var span = Assert.ContainsSingle(result.Spans);
            Assert.AreEqual("rich.operation", span.Name);
            Assert.AreEqual("11111111111111111111111111111111", span.TraceId);
            Assert.AreEqual(SpanKind.Consumer, span.Kind);
            Assert.AreEqual(ActivityStatusCode.Error, span.Status);
            Assert.AreEqual("rich-scope", span.Scope?.Name);
            Assert.AreEqual(1L, Assert.ContainsSingle(result.Receipts).Accepted);
        }
    }

    [TestMethod]
    public async Task Canceled_startup_preserves_cancellation_instead_of_returning_a_live_receiver()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => OtlpReceiver.StartAsync(cancellation.Token));
    }

    [TestMethod]
    public async Task Capture_timeout_fails_instead_of_accepting_missing_evidence()
    {
        var previous = Activity.Current;
        await Assert.ThrowsExactlyAsync<TimeoutException>(() => TelemetryCapture.TraceAsync(_ => Task.CompletedTask,
            trace => trace.Spans.Count > 0, TimeSpan.Zero, TestContext.CancellationToken));
        Assert.AreSame(previous, Activity.Current);
    }

    private Task<TraceEvidence> CaptureAsync(int number)
    {
        return TelemetryCapture.TraceAsync(async capture =>
            {
                Assert.IsNotNull(Activity.Current);
                Assert.AreEqual("qyl.test", Activity.Current.OperationName);
                var request = RichSpan();
                request.ResourceSpans[0].ScopeSpans[0].Spans[0].TraceId = Id(capture.TraceId);
                request.ResourceSpans[0].ScopeSpans[0].Spans[0].Name = "capture." + number;
                request.ResourceSpans.Add(RichSpan().ResourceSpans[0]);
                using var response = await ReceiverTests.PostAsync(capture.Endpoint, request.ToByteArray(),
                    TestContext.CancellationToken);
                response.EnsureSuccessStatusCode();
            }, trace => trace.Spans.Count == 1 && trace.Capture.Spans.Count == 2,
            cancellationToken: TestContext.CancellationToken);
    }
}