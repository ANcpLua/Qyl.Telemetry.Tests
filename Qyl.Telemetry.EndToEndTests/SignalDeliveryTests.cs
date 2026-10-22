using System.Diagnostics;
using System.Net;
using Qyl.Api.Contracts.Common;
using Qyl.Api.Contracts.OTel.Metrics;

namespace Qyl.Telemetry.EndToEndTests;

[TracePerTest]
[DoNotParallelize]
public sealed class SignalDeliveryTests(TestContext context) : CaptureDeliveryFixture(context)
{
    [TestMethod]
    [NoErrorTelemetry]
    [ExportsCleanly]
    public async Task ApplicationEvent_StoredAsLog_KeepsTheServerSpanAndResource()
    {
        var evidence = await SendRequestAsync();
        var server = Assert.ContainsSingle(evidence.Spans.Where(span => span.Service == ProbeService));
        var completed = Assert.ContainsSingle(server.Events.Where(item => item.Name == ProbeSignals.CompletedEvent));
        Assert.AreEqual(ProbeSignals.RequestId, completed.Attributes[ProbeSignals.RequestAttribute]);
        Assert.IsGreaterThanOrEqualTo(server.StartTimeUnixNano, completed.TimeUnixNano);
        Assert.IsLessThanOrEqualTo(server.EndTimeUnixNano, completed.TimeUnixNano);

        var capture = await Application.Telemetry.AwaitAsync(snapshot => snapshot.Logs.Any(log =>
            log.TraceId == evidence.TraceId && log.EventName == ProbeSignals.CompletedLog), Context.CancellationToken);
        var original = Assert.ContainsSingle(capture.Logs.Where(log => log.TraceId == evidence.TraceId
            && log.EventName == ProbeSignals.CompletedLog));
        Assert.AreEqual(server.SpanId, original.SpanId);
        Assert.AreEqual(server.Resource.Attributes["service.instance.id"], original.Resource.Attributes["service.instance.id"]);
        var stored = await Application.Telemetry.Collector.Client.WaitForLogsAsync(evidence.TraceId,
            logs => logs.Any(log => log.EventName == ProbeSignals.CompletedLog), Context.CancellationToken);
        var log = Assert.ContainsSingle(stored.Where(log => log.EventName == ProbeSignals.CompletedLog));
        Assert.AreEqual(server.SpanId, log.SpanId);
        Assert.AreEqual(evidence.TraceId, log.TraceId);
        Assert.Contains(ProbeSignals.RequestId, Assert.IsInstanceOfType<AttributeValue.StringValue>(log.Body).Value);
    }

    [TestMethod]
    [NoErrorTelemetry]
    [ExportsCleanly]
    public async Task ApplicationCounter_Stored_IsAttributedByResource()
    {
        var start = Application.Telemetry.Collector.TimeProvider.GetUtcNow().AddSeconds(-1);
        var evidence = await SendRequestAsync();
        var server = Assert.ContainsSingle(evidence.Spans.Where(span => span.Service == ProbeService));
        var instance = server.Resource.Attributes["service.instance.id"];
        var capture = await Application.Telemetry.AwaitAsync(snapshot => snapshot.Metrics.Any(metric =>
            metric.Name == ProbeSignals.CounterName && Equals(metric.Resource.Attributes["service.instance.id"], instance)),
            Context.CancellationToken);
        var counter = capture.Metrics.Last(metric => metric.Name == ProbeSignals.CounterName
            && Equals(metric.Resource.Attributes["service.instance.id"], instance));
        Assert.AreEqual(ProbeSignals.CounterUnit, counter.Unit);
        Assert.AreEqual(ProbeSignals.MeterName, counter.Scope?.Name);
        var sum = Assert.IsInstanceOfType<SumData>(counter.Data);
        Assert.IsTrue(sum.IsMonotonic);
        Assert.AreEqual(1L, Assert.IsInstanceOfType<long>(Assert.ContainsSingle(sum.Points).Value));
        var end = Application.Telemetry.Collector.TimeProvider.GetUtcNow().AddSeconds(1);
        var stored = await Application.Telemetry.Collector.Client.WaitForMetricAsync(ProbeSignals.CounterName, start, end,
            metric => metric.Series.Any(series => series.Buckets.Any(bucket => bucket.Value is not null)),
            cancellationToken: Context.CancellationToken);
        Assert.AreEqual(ProbeSignals.CounterUnit, stored.Unit);
        Assert.AreEqual(MetricKind.Sum, stored.Kind);
        Assert.IsFalse(stored.Truncated);
        var persisted = Assert.ContainsSingle(stored.Series).Buckets.Where(bucket => bucket.Value is not null).ToArray();
        Assert.IsNotEmpty(persisted);
        foreach (var bucket in persisted) Assert.AreEqual(1d, bucket.Value, "The stored cumulative counter must retain its measured value.");
    }

    [TestMethod]
    [NoErrorTelemetry]
    [ExportsCleanly]
    [DataRow(NativeMetric.ServerDuration)]
    [DataRow(NativeMetric.CpuTime)]
    [DataRow(NativeMetric.WorkingSet)]
    public async Task NativeMetrics_Stored_KeepTypeUnitScopeAndProducer(NativeMetric metric)
    {
        var expected = metric switch
        {
            NativeMetric.ServerDuration => (Name: "http.server.request.duration", Unit: "s", Scope: "Microsoft.AspNetCore.Hosting", Kind: typeof(HistogramData)),
            NativeMetric.CpuTime => (Name: "dotnet.process.cpu.time", Unit: "s", Scope: "System.Runtime", Kind: typeof(SumData)),
            NativeMetric.WorkingSet => (Name: "dotnet.process.memory.working_set", Unit: "By", Scope: "System.Runtime", Kind: typeof(SumData)),
            _ => throw new ArgumentOutOfRangeException(nameof(metric))
        };
        var evidence = await SendRequestAsync();
        var instance = Assert.ContainsSingle(evidence.Spans.Where(span => span.Service == ProbeService)).Resource.Attributes["service.instance.id"];
        var capture = await Application.Telemetry.AwaitAsync(snapshot => snapshot.Metrics.Any(item =>
            item.Name == expected.Name && Equals(item.Resource.Attributes["service.instance.id"], instance)), Context.CancellationToken);
        var observed = capture.Metrics.Where(item => item.Name == expected.Name
            && Equals(item.Resource.Attributes["service.instance.id"], instance)).ToArray();
        Assert.IsNotEmpty(observed);
        foreach (var item in observed)
        {
            Assert.AreEqual(expected.Unit, item.Unit);
            Assert.AreEqual(expected.Scope, item.Scope?.Name);
            Assert.IsInstanceOfType(item.Data, expected.Kind);
            Assert.AreEqual("dotnet", item.Resource.Attributes["telemetry.sdk.language"]);
        }
    }

    [TestMethod]
    public async Task ApplicationRequest_Failed_KeepsErrorStatusAndExceptionEvidence()
    {
        var evidence = await SendRequestAsync("failure", HttpStatusCode.InternalServerError);
        var server = Assert.ContainsSingle(evidence.Spans.Where(span => span.Service == ProbeService));
        Assert.AreEqual(ActivityStatusCode.Error, server.Status);
        var completed = await Application.Telemetry.AwaitAsync(snapshot => snapshot.Logs.Any(log =>
            log.TraceId == evidence.TraceId && log.Scope?.Name == "Microsoft.AspNetCore.Server.Kestrel"), Context.CancellationToken);
        var serverLog = Assert.ContainsSingle(completed.Logs.Where(log =>
            log.TraceId == evidence.TraceId && log.Scope?.Name == "Microsoft.AspNetCore.Server.Kestrel"));
        Assert.AreEqual(server.SpanId, serverLog.SpanId);
        Assert.IsInstanceOfType<string>(serverLog.Attributes["ConnectionId"]);
        Assert.Contains(receipt => receipt.Warnings.Contains("Coalesced identical OTLP attribute: ConnectionId."), completed.Receipts);
        Assert.Contains(item => item.Name == "exception" && item.Attributes.TryGetValue("exception.message", out var message)
            && Equals(message, ProbeSignals.FailureMessage), server.Events,
            $"Observed events: {string.Join("; ", server.Events.Select(item => item.Name + ":" + string.Join(", ", item.Attributes.Select(pair => pair.Key + "=" + pair.Value))))}; error.type={server.GetString("error.type")}");
    }

    public enum NativeMetric { ServerDuration, CpuTime, WorkingSet }
}
