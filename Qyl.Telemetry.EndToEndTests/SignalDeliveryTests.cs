using System.Diagnostics;
using System.Net;
using Qyl.Api.Contracts.Common;
using Qyl.Api.Contracts.OTel.Metrics;
using Qyl.Telemetry.SemanticConventions;
using Qyl.Telemetry.Tests.Collector;

namespace Qyl.Telemetry.EndToEndTests;

[TracePerTest]
[DoNotParallelize]
public sealed class SignalDeliveryTests(TestContext context) : CaptureDeliveryFixture(context)
{
    private const string ServiceInstanceId = "service.instance.id";

    [TestMethod]
    [NoErrorTelemetry]
    [ExportsCleanly]
    public async Task ApplicationEvent_StoredAsLog_KeepsTheServerSpanAndResource()
    {
        var response = await SendRequestAsync();
        foreach (var (header, added) in response.AddedHeaders)
            Assert.IsTrue(added, $"Could not add request header '{header}'.");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.IsTrue(response.InformationLoggingEnabled);
        _ = Assert.That.HasServerSpanUnderTheTestClient(response.RoundTrip, "GET /capture");
        var evidence = response.RoundTrip.Evidence;
        var server = Assert.ContainsSingle(evidence.Spans.Where(span => span.Service == InstrumentedApplicationFixture.ServiceName));
        var completed = Assert.ContainsSingle(server.Events.Where(item => item.Name == ProbeSignals.CompletedEvent));
        Assert.AreEqual(ProbeSignals.RequestId, completed.Attributes[ProbeSignals.RequestAttribute]);
        Assert.IsGreaterThanOrEqualTo(server.StartTimeUnixNano, completed.TimeUnixNano);
        Assert.IsLessThanOrEqualTo(server.EndTimeUnixNano, completed.TimeUnixNano);

        var capture = await Application.Telemetry.AwaitAsync(snapshot => snapshot.Logs.Any(log =>
            log.TraceId == evidence.TraceId && log.EventName == ProbeSignals.CompletedLog), Context.CancellationToken);
        var original = Assert.ContainsSingle(capture.Logs.Where(log => log.TraceId == evidence.TraceId
            && log.EventName == ProbeSignals.CompletedLog));
        Assert.AreEqual(server.SpanId, original.SpanId);
        Assert.AreEqual(server.Resource.Attributes[ServiceInstanceId], original.Resource.Attributes[ServiceInstanceId]);
        Assert.AreEqual(server.Service, original.Resource.Attributes["service.name"]);
        var stored = await Application.Telemetry.Collector.Client.WaitForLogsAsync(evidence.TraceId,
            logs => logs.Any(log => log.EventName == ProbeSignals.CompletedLog), Context.CancellationToken);
        var log = Assert.ContainsSingle(stored.Where(log => log.EventName == ProbeSignals.CompletedLog));
        Assert.AreEqual(server.SpanId, log.SpanId);
        Assert.AreEqual(evidence.TraceId, log.TraceId);
        Assert.Contains(ProbeSignals.RequestId, Assert.IsInstanceOfType<AttributeValue.StringValue>(log.Body).Value);
        Assert.AreEqual(server.Service, log.Resource.ServiceName);
        Assert.IsNotNull(log.Resource.Attributes);
        Assert.DoesNotContain(attribute => attribute.Key == ServiceInstanceId, log.Resource.Attributes);
        foreach (var (key, value) in original.Resource.Attributes.Where(pair => pair.Value is string && RegistryPolicy.IsPersisted(pair.Key)))
        {
            var retained = Assert.ContainsSingle(log.Resource.Attributes.Where(attribute => attribute.Key == key));
            Assert.AreEqual((string)value!, Assert.IsInstanceOfType<AttributeValue.StringValue>(retained.Value).Value);
        }
    }

    [TestMethod]
    [NoErrorTelemetry]
    [ExportsCleanly]
    public async Task ApplicationCounter_Stored_IsAttributedByResource()
    {
        var start = Application.Telemetry.Collector.TimeProvider.GetUtcNow().AddSeconds(-1);
        var response = await SendRequestAsync();
        foreach (var (header, added) in response.AddedHeaders)
            Assert.IsTrue(added, $"Could not add request header '{header}'.");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.IsTrue(response.InformationLoggingEnabled);
        _ = Assert.That.HasServerSpanUnderTheTestClient(response.RoundTrip, "GET /capture");
        var evidence = response.RoundTrip.Evidence;
        var server = Assert.ContainsSingle(evidence.Spans.Where(span => span.Service == InstrumentedApplicationFixture.ServiceName));
        var instance = Assert.IsInstanceOfType<string>(server.Resource.Attributes[ServiceInstanceId]);
        var capture = await Application.Telemetry.AwaitAsync(snapshot => snapshot.Metrics.Any(metric =>
            metric.Name == ProbeSignals.CounterName && Equals(metric.Resource.Attributes[ServiceInstanceId], instance)),
            Context.CancellationToken);
        var counter = capture.Metrics.Last(metric => metric.Name == ProbeSignals.CounterName
            && Equals(metric.Resource.Attributes[ServiceInstanceId], instance));
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
        Assert.AreSequenceEqual(Enumerable.Repeat<double?>(1d, persisted.Length), persisted.Select(bucket => bucket.Value),
            "The stored cumulative counter must retain its measured value.");
        var storedSeries = await Application.Telemetry.Collector.Client.WaitForMetricSeriesAsync(ProbeSignals.CounterName,
            items => items.Any(item => item.ServiceName == server.Service), Context.CancellationToken);
        var attributed = storedSeries.Where(item => item.ServiceName == server.Service).ToArray();
        Assert.IsNotEmpty(attributed);
        foreach (var item in attributed)
        {
            Assert.AreEqual(ProbeSignals.CounterName, item.Name);
            Assert.AreEqual(server.Service, item.ServiceName);
            Assert.IsNotNull(item.Resource);
            Assert.AreEqual(server.Service, item.Resource.ServiceName);
            Assert.IsNotNull(item.Resource.Attributes);
            Assert.DoesNotContain(attribute => attribute.Key == ServiceInstanceId, item.Resource.Attributes);
            foreach (var (key, value) in counter.Resource.Attributes.Where(pair => pair.Value is string && RegistryPolicy.IsPersisted(pair.Key)))
            {
                var retained = Assert.ContainsSingle(item.Resource.Attributes.Where(attribute => attribute.Key == key));
                Assert.AreEqual((string)value!, Assert.IsInstanceOfType<AttributeValue.StringValue>(retained.Value).Value);
            }
        }
    }

    [TestMethod]
    [NoErrorTelemetry]
    [ExportsCleanly]
    [DataRow("http.server.request.duration", "s", "Microsoft.AspNetCore.Hosting", typeof(HistogramData), MetricKind.Histogram)]
    [DataRow("dotnet.process.cpu.time", "s", "System.Runtime", typeof(SumData), MetricKind.Sum)]
    [DataRow("dotnet.process.memory.working_set", "By", "System.Runtime", typeof(SumData), MetricKind.Sum)]
    public async Task NativeMetrics_Stored_KeepTypeUnitScopeAndProducer(string name, string unit, string scope, Type kind,
        MetricKind storedKind)
    {
        var response = await SendRequestAsync();
        foreach (var (header, added) in response.AddedHeaders)
            Assert.IsTrue(added, $"Could not add request header '{header}'.");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.IsTrue(response.InformationLoggingEnabled);
        _ = Assert.That.HasServerSpanUnderTheTestClient(response.RoundTrip, "GET /capture");
        var evidence = response.RoundTrip.Evidence;
        var server = Assert.ContainsSingle(evidence.Spans.Where(span => span.Service == InstrumentedApplicationFixture.ServiceName));
        var instance = Assert.IsInstanceOfType<string>(server.Resource.Attributes[ServiceInstanceId]);
        var capture = await Application.Telemetry.AwaitAsync(snapshot => snapshot.Metrics.Any(item =>
            item.Name == name && Equals(item.Resource.Attributes[ServiceInstanceId], instance)), Context.CancellationToken);
        var observed = capture.Metrics.Where(item => item.Name == name
            && Equals(item.Resource.Attributes[ServiceInstanceId], instance)).ToArray();
        Assert.IsNotEmpty(observed);
        Assert.AreSequenceEqual(Enumerable.Repeat<(string, string?, Type, object?)>((unit, scope, kind, "dotnet"), observed.Length),
            observed.Select(item => (item.Unit, item.Scope?.Name, item.Data.GetType(),
                item.Resource.Attributes["telemetry.sdk.language"])));
        var storedSeries = await Application.Telemetry.Collector.Client.WaitForMetricSeriesAsync(name,
            items => items.Any(item => item.ServiceName == server.Service), Context.CancellationToken);
        var persisted = storedSeries.Where(item => item.ServiceName == server.Service).ToArray();
        Assert.IsNotEmpty(persisted);
        foreach (var item in persisted)
        {
            Assert.AreEqual(name, item.Name);
            Assert.AreEqual(storedKind, item.Kind);
            Assert.AreEqual(unit, item.Unit);
            Assert.AreEqual(scope, item.InstrumentationScope?.ScopeName);
            Assert.AreEqual(server.Service, item.ServiceName);
            Assert.IsNotNull(item.Resource);
            Assert.AreEqual(server.Service, item.Resource.ServiceName);
            Assert.IsNotNull(item.Resource.Attributes);
            Assert.DoesNotContain(attribute => attribute.Key == ServiceInstanceId, item.Resource.Attributes);
            foreach (var (key, value) in observed[0].Resource.Attributes.Where(pair => pair.Value is string && RegistryPolicy.IsPersisted(pair.Key)))
            {
                var retained = Assert.ContainsSingle(item.Resource.Attributes.Where(attribute => attribute.Key == key));
                Assert.AreEqual((string)value!, Assert.IsInstanceOfType<AttributeValue.StringValue>(retained.Value).Value);
            }
            var language = Assert.ContainsSingle(item.Resource.Attributes.Where(attribute => attribute.Key == "telemetry.sdk.language"));
            Assert.AreEqual("dotnet", Assert.IsInstanceOfType<AttributeValue.StringValue>(language.Value).Value);
        }
    }

    [TestMethod]
    public async Task ApplicationRequest_Failed_KeepsErrorStatusAndExceptionEvidence()
    {
        var response = await SendRequestAsync("failure");
        foreach (var (header, added) in response.AddedHeaders)
            Assert.IsTrue(added, $"Could not add request header '{header}'.");
        Assert.AreEqual(HttpStatusCode.InternalServerError, response.StatusCode);
        _ = Assert.That.HasServerSpanUnderTheTestClient(response.RoundTrip, "GET /failure");
        var evidence = response.RoundTrip.Evidence;
        var server = Assert.ContainsSingle(evidence.Spans.Where(span => span.Service == InstrumentedApplicationFixture.ServiceName));
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

}
