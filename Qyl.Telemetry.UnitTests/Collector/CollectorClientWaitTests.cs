using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Time.Testing;
using Qyl.Telemetry.Tests.Collector;

namespace Qyl.Telemetry.UnitTests.Collector;

/// <summary>Drives the Collector evidence waits on a fake clock: polling pace, deadline and caller cancellation.</summary>
/// <param name="testContext">The runner context supplies cancellation.</param>
[TestClass]
[TestCategory("Fast")]
public class CollectorClientWaitTests(TestContext testContext)
{
    private const string TraceId = "trace";
    private const string EventName = "completed";
    private const string StoredTrace = """{"trace_id":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","span_count":0,"duration_ns":"0","start_time":"2026-10-23T00:00:00Z","end_time":"2026-10-23T00:00:00Z","services":[],"has_error":false,"spans":[]}""";

    [TestMethod]
    public async Task Should_ThrowTimeoutException_When_WaitForTrace_TraceMissingUntilTheDeadline()
    {
        var clock = new FakeTimeProvider();
        using var collector = new FakeCollector([null]);
        var waiting = collector.Client(clock).WaitForTraceAsync(TraceId, _ => true, testContext.CancellationToken);
        await collector.FirstRead.WaitAsync(testContext.CancellationToken);
        clock.Advance(CollectorClient.WaitDeadline);
        _ = await Assert.ThrowsExactlyAsync<TimeoutException>(() => waiting);
    }

    [TestMethod]
    public async Task Should_KeepWaiting_When_WaitForTrace_TraceMissingJustBeforeTheDeadline()
    {
        var clock = new FakeTimeProvider();
        using var collector = new FakeCollector([null]);
        var waiting = collector.Client(clock).WaitForTraceAsync(TraceId, _ => true, testContext.CancellationToken);
        await collector.FirstRead.WaitAsync(testContext.CancellationToken);
        clock.Advance(CollectorClient.WaitDeadline - TimeSpan.FromTicks(1));
        Assert.IsFalse(waiting.IsCompleted);
    }

    [TestMethod]
    public async Task Should_ReturnTheStoredTrace_When_WaitForTrace_TraceStoredAfterOnePollingInterval()
    {
        var clock = new FakeTimeProvider();
        using var collector = new FakeCollector([null, StoredTrace]);
        var waiting = collector.Client(clock).WaitForTraceAsync(TraceId, _ => true, testContext.CancellationToken);
        await collector.FirstRead.WaitAsync(testContext.CancellationToken);
        clock.Advance(CollectorClient.PollingInterval);
        var trace = await waiting.WaitAsync(testContext.CancellationToken);
        Assert.AreEqual(0, trace.GetProperty("spans").GetArrayLength());
    }

    [TestMethod]
    public async Task Should_ReadOnce_When_WaitForTrace_PollingIntervalNotElapsed()
    {
        var clock = new FakeTimeProvider();
        using var collector = new FakeCollector([null]);
        _ = collector.Client(clock).WaitForTraceAsync(TraceId, _ => true, testContext.CancellationToken);
        await collector.FirstRead.WaitAsync(testContext.CancellationToken);
        clock.Advance(CollectorClient.PollingInterval - TimeSpan.FromTicks(1));
        Assert.AreEqual(1, collector.Reads);
    }

    [TestMethod]
    public async Task Should_ReadAgain_When_WaitForTrace_ConditionUnmetAfterOnePollingInterval()
    {
        var clock = new FakeTimeProvider();
        using var collector = new FakeCollector([StoredTrace]);
        _ = collector.Client(clock).WaitForTraceAsync(TraceId, _ => false, testContext.CancellationToken);
        await collector.FirstRead.WaitAsync(testContext.CancellationToken);
        clock.Advance(CollectorClient.PollingInterval);
        await collector.SecondRead.WaitAsync(testContext.CancellationToken);
        Assert.AreEqual(2, collector.Reads);
    }

    [TestMethod]
    public async Task Should_ThrowOperationCanceledException_When_WaitForTrace_CallerCancelsBeforeTheDeadline()
    {
        var clock = new FakeTimeProvider();
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(testContext.CancellationToken);
        using var collector = new FakeCollector([null]);
        var waiting = collector.Client(clock).WaitForTraceAsync(TraceId, _ => true, caller.Token);
        await collector.FirstRead.WaitAsync(testContext.CancellationToken);
        await caller.CancelAsync();
        _ = await Assert.ThrowsAsync<OperationCanceledException>(() => waiting);
    }

    [TestMethod]
    public async Task Should_ReturnTheStoredEvent_When_WaitForSpanEvents_EventStoredAfterOnePollingInterval()
    {
        var clock = new FakeTimeProvider();
        using var collector = new FakeCollector([StoredTrace, TraceWithEvent]);
        var waiting = collector.Client(clock).WaitForSpanEventsAsync(TraceId, events => events.Count > 0,
            testContext.CancellationToken);
        await collector.FirstRead.WaitAsync(testContext.CancellationToken);
        clock.Advance(CollectorClient.PollingInterval);
        var events = await waiting.WaitAsync(testContext.CancellationToken);
        Assert.AreEqual(EventName, Assert.ContainsSingle(events).Name);
    }

    [TestMethod]
    public async Task Should_ThrowTimeoutException_When_WaitForSpanEvents_EventMissingUntilTheDeadline()
    {
        var clock = new FakeTimeProvider();
        using var collector = new FakeCollector([StoredTrace]);
        var waiting = collector.Client(clock).WaitForSpanEventsAsync(TraceId, events => events.Count > 0,
            testContext.CancellationToken);
        await collector.FirstRead.WaitAsync(testContext.CancellationToken);
        clock.Advance(CollectorClient.WaitDeadline);
        _ = await Assert.ThrowsExactlyAsync<TimeoutException>(() => waiting);
    }

    private static string TraceWithEvent
    {
        get
        {
            var trace = JsonNode.Parse(StoredTrace)!.AsObject();
            trace["span_count"] = 1;
            trace["spans"] = JsonNode.Parse("""[{"span_id":"1111111111111111","trace_id":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","name":"operation","kind":1,"start_time_unix_nano":"0","end_time_unix_nano":"0","status":{"code":0},"resource":{"service_name":"test"},"events":[{"name":"completed","time_unix_nano":"0"}]}]""");
            return trace.ToJsonString();
        }
    }

    private sealed class FakeCollector(IReadOnlyList<string?> responses) : IDisposable
    {
        private readonly TaskCompletionSource _firstRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _secondRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<HttpClient> _clients = [];
        private int _reads;

        public Task FirstRead => _firstRead.Task;
        public Task SecondRead => _secondRead.Task;
        public int Reads => Volatile.Read(ref _reads);

        public CollectorClient Client(TimeProvider clock)
        {
            var client = new HttpClient(new StubHandler(_ => Respond())) { BaseAddress = new Uri("https://qyl.test/") };
            _clients.Add(client);
            return new CollectorClient(client, clock);
        }

        public void Dispose()
        {
            foreach (var client in _clients) client.Dispose();
        }

        private HttpResponseMessage Respond()
        {
            var read = Interlocked.Increment(ref _reads);
            var body = responses[Math.Min(read - 1, responses.Count - 1)];
            switch (read)
            {
                case 1:
                    _firstRead.TrySetResult();
                    break;
                case 2:
                    _secondRead.TrySetResult();
                    break;
            }
            return body is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
}
