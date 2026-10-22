using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Qyl.Api.Contracts.OTel.Traces;
using Qyl.Telemetry.Tests.Collector;

namespace Qyl.Telemetry.UnitTests.Collector;

/// <summary>Drives the Collector evidence waits on a fake clock: polling pace, deadline and caller cancellation.</summary>
/// <param name="testContext">The runner context supplies cancellation.</param>
[TestClass]
public sealed class CollectorClientWaitTests(TestContext testContext)
{
    private const string TraceId = "trace";
    private const string EventName = "completed";
    private const string StoredTrace = """{"spans":[]}""";

    [TestMethod]
    public async Task WaitForTrace_TraceMissingUntilTheDeadline_ThrowsTimeoutException()
    {
        var clock = new FakeTimeProvider();
        using var collector = new FakeCollector(_ => null);
        var waiting = collector.Client(clock).WaitForTraceAsync(TraceId, _ => true, testContext.CancellationToken);
        await collector.FirstRead.WaitAsync(testContext.CancellationToken);
        clock.Advance(CollectorClient.WaitDeadline);
        _ = await Assert.ThrowsExactlyAsync<TimeoutException>(() => waiting);
    }

    [TestMethod]
    public async Task WaitForTrace_TraceMissingJustBeforeTheDeadline_KeepsWaiting()
    {
        var clock = new FakeTimeProvider();
        using var collector = new FakeCollector(_ => null);
        var waiting = collector.Client(clock).WaitForTraceAsync(TraceId, _ => true, testContext.CancellationToken);
        await collector.FirstRead.WaitAsync(testContext.CancellationToken);
        clock.Advance(CollectorClient.WaitDeadline - TimeSpan.FromTicks(1));
        Assert.IsFalse(waiting.IsCompleted);
    }

    [TestMethod]
    public async Task WaitForTrace_TraceStoredAfterOnePollingInterval_ReturnsTheStoredTrace()
    {
        var clock = new FakeTimeProvider();
        using var collector = new FakeCollector(read => read == 1 ? null : StoredTrace);
        var waiting = collector.Client(clock).WaitForTraceAsync(TraceId, _ => true, testContext.CancellationToken);
        await collector.FirstRead.WaitAsync(testContext.CancellationToken);
        clock.Advance(CollectorClient.PollingInterval);
        var trace = await waiting.WaitAsync(testContext.CancellationToken);
        Assert.AreEqual(0, trace.GetProperty("spans").GetArrayLength());
    }

    [TestMethod]
    public async Task WaitForTrace_PollingIntervalNotElapsed_ReadsOnce()
    {
        var clock = new FakeTimeProvider();
        using var collector = new FakeCollector(_ => null);
        _ = collector.Client(clock).WaitForTraceAsync(TraceId, _ => true, testContext.CancellationToken);
        await collector.FirstRead.WaitAsync(testContext.CancellationToken);
        clock.Advance(CollectorClient.PollingInterval - TimeSpan.FromTicks(1));
        Assert.AreEqual(1, collector.Reads);
    }

    [TestMethod]
    public async Task WaitForTrace_ConditionUnmetAfterOnePollingInterval_ReadsAgain()
    {
        var clock = new FakeTimeProvider();
        using var collector = new FakeCollector(_ => StoredTrace);
        _ = collector.Client(clock).WaitForTraceAsync(TraceId, _ => false, testContext.CancellationToken);
        await collector.FirstRead.WaitAsync(testContext.CancellationToken);
        clock.Advance(CollectorClient.PollingInterval);
        await collector.SecondRead.WaitAsync(testContext.CancellationToken);
        Assert.AreEqual(2, collector.Reads);
    }

    [TestMethod]
    public async Task WaitForTrace_CallerCancelsBeforeTheDeadline_ThrowsOperationCanceledException()
    {
        var clock = new FakeTimeProvider();
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(testContext.CancellationToken);
        using var collector = new FakeCollector(_ => null);
        var waiting = collector.Client(clock).WaitForTraceAsync(TraceId, _ => true, caller.Token);
        await collector.FirstRead.WaitAsync(testContext.CancellationToken);
        await caller.CancelAsync();
        _ = await Assert.ThrowsAsync<OperationCanceledException>(() => waiting);
    }

    [TestMethod]
    public async Task WaitForSpanEvents_EventStoredAfterOnePollingInterval_ReturnsTheStoredEvent()
    {
        var clock = new FakeTimeProvider();
        using var collector = new FakeCollector(read => read == 1 ? StoredTrace : TraceWithEvent);
        var waiting = collector.Client(clock).WaitForSpanEventsAsync(TraceId, events => events.Count > 0,
            testContext.CancellationToken);
        await collector.FirstRead.WaitAsync(testContext.CancellationToken);
        clock.Advance(CollectorClient.PollingInterval);
        var events = await waiting.WaitAsync(testContext.CancellationToken);
        Assert.AreEqual(EventName, Assert.ContainsSingle(events).Name);
    }

    [TestMethod]
    public async Task WaitForSpanEvents_EventMissingUntilTheDeadline_ThrowsTimeoutException()
    {
        var clock = new FakeTimeProvider();
        using var collector = new FakeCollector(_ => StoredTrace);
        var waiting = collector.Client(clock).WaitForSpanEventsAsync(TraceId, events => events.Count > 0,
            testContext.CancellationToken);
        await collector.FirstRead.WaitAsync(testContext.CancellationToken);
        clock.Advance(CollectorClient.WaitDeadline);
        _ = await Assert.ThrowsExactlyAsync<TimeoutException>(() => waiting);
    }

    private static string TraceWithEvent => JsonSerializer.Serialize(new
    {
        spans = new[] { new { events = new[] { new SpanEvent { Name = EventName, TimeUnixNano = 0 } } } }
    });

    private sealed class FakeCollector(Func<int, string?> respond) : IDisposable
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
            var client = new HttpClient(new Handler(Respond)) { BaseAddress = new Uri("https://qyl.test/") };
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
            var body = respond(read);
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

    private sealed class Handler(Func<HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(respond());
        }
    }
}
