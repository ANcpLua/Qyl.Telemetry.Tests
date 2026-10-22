using System.Diagnostics;
using Google.Protobuf;
using JetBrains.Annotations;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Logs.V1;
using OpenTelemetry.Proto.Metrics.V1;
using OpenTelemetry.Proto.Resource.V1;
using OpenTelemetry.Proto.Trace.V1;
using Qyl.Telemetry.SemanticConventions;
using static Qyl.Telemetry.Tests.Evidence.OtlpEvidence;

namespace Qyl.Telemetry.IntegrationTests.Receiver;

[TestClass]
public sealed class OptionalWireTests
{
    [UsedImplicitly] public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task OtlpReceiver_AbsentResourceAndScopeMetadata_StayAbsent()
    {
        await using var receiver = await OtlpReceiver.StartAsync(TestContext.CancellationToken);
        var request = Traces(null,
            new Span { Name = "minimal", TraceId = Id("11111111111111111111111111111111"), SpanId = Id("2222222222222222") });
        using var response =
            await OtlpHttp.PostAsync(receiver.Endpoint, request.ToByteArray(), TestContext.CancellationToken);
        response.EnsureSuccessStatusCode();
        var span = Assert.ContainsSingle(receiver.Snapshot().Spans);
        Assert.AreEqual("minimal", span.Name);
        Assert.AreEqual("11111111111111111111111111111111", span.TraceId);
        Assert.AreEqual(SpanKind.Unspecified, span.Kind);
        Assert.AreEqual(ActivityStatusCode.Unset, span.Status);
        Assert.IsNull(span.Scope);
        Assert.IsNull(span.Resource.SchemaUrl);
        Assert.IsEmpty(span.Resource.Attributes);
        Assert.IsEmpty(span.Resource.EntityReferences);
        Assert.IsNull(span.ParentSpanId);
        Assert.IsNull(span.StatusDescription);
        Assert.IsNull(span.TraceState);
    }

    [TestMethod]
    public async Task OtlpReceiver_ScopeSchemaWithoutScopeMessage_IsKept()
    {
        await using var receiver = await OtlpReceiver.StartAsync(TestContext.CancellationToken);
        var request = RichSpan();
        request.ResourceSpans[0].ScopeSpans[0].Scope = null;
        using var response =
            await OtlpHttp.PostAsync(receiver.Endpoint, request.ToByteArray(), TestContext.CancellationToken);
        response.EnsureSuccessStatusCode();
        var span = Assert.ContainsSingle(receiver.Snapshot().Spans);
        Assert.AreEqual("rich.operation", span.Name);
        Assert.AreEqual("11111111111111111111111111111111", span.TraceId);
        Assert.IsNotNull(span.Scope);
        Assert.AreEqual("scope-schema", span.Scope.SchemaUrl);
        Assert.IsNull(span.Scope.Name);
        Assert.IsNull(span.Scope.Version);
        Assert.IsEmpty(span.Scope.Attributes);
        Assert.AreEqual(0u, span.Scope.DroppedAttributesCount);
    }

    [TestMethod]
    [DataRow(MetricShapes.Histogram)]
    [DataRow(MetricShapes.Exponential)]
    public async Task OtlpReceiverMetrics_OptionalDistributionFieldsAbsent_StayNullInsteadOfZero(string kind)
    {
        await using var receiver = await OtlpReceiver.StartAsync(TestContext.CancellationToken);
        var metric = MetricFixture.Of(kind);
        if (kind == MetricShapes.Histogram)
        {
            var p = metric.Histogram.DataPoints[0];
            p.ClearSum();
            p.ClearMin();
            p.ClearMax();
        }
        else
        {
            var p = metric.ExponentialHistogram.DataPoints[0];
            p.ClearSum();
            p.ClearMin();
            p.ClearMax();
            p.Positive = null;
            p.Negative = null;
        }

        var request = Metrics(null, metric);
        using var response = await OtlpHttp.PostAsync(receiver.Endpoint, request.ToByteArray(),
            TestContext.CancellationToken, Signals.Metrics);
        response.EnsureSuccessStatusCode();
        var observed = Assert.ContainsSingle(receiver.Snapshot().Metrics);
        Assert.AreEqual("metric." + kind, observed.Name);
        Assert.IsNull(observed.Resource.SchemaUrl);
        Assert.IsNull(observed.Scope);
        if (kind == MetricShapes.Histogram)
        {
            var p = Assert.ContainsSingle(Assert.IsInstanceOfType<HistogramData>(observed.Data).Points);
            Assert.IsNull(p.Sum);
            Assert.IsNull(p.Min);
            Assert.IsNull(p.Max);
        }
        else
        {
            var p = Assert.ContainsSingle(Assert.IsInstanceOfType<ExponentialHistogramData>(observed.Data).Points);
            Assert.IsNull(p.Sum);
            Assert.IsNull(p.Min);
            Assert.IsNull(p.Max);
            Assert.IsNull(p.Positive);
            Assert.IsNull(p.Negative);
        }
    }

    [TestMethod]
    public async Task OtlpReceiverLogs_UncorrelatedWithoutBodyOrContext_IsKeptInTheCapture()
    {
        await using var receiver = await OtlpReceiver.StartAsync(TestContext.CancellationToken);
        var request = Logs(null, new InstrumentationScope { Name = "background" },
            new OpenTelemetry.Proto.Logs.V1.LogRecord { EventName = "BackgroundError", SeverityNumber = SeverityNumber.Error });
        using var response = await OtlpHttp.PostAsync(receiver.Endpoint, request.ToByteArray(),
            TestContext.CancellationToken, Signals.Logs);
        response.EnsureSuccessStatusCode();
        var snapshot = receiver.Snapshot();
        var log = Assert.ContainsSingle(snapshot.Logs);
        Assert.AreEqual("BackgroundError", log.EventName);
        Assert.AreEqual(17, log.SeverityNumber);
        Assert.AreEqual("background", log.Scope?.Name);
        Assert.IsNull(log.TraceId);
        Assert.IsNull(log.SpanId);
        Assert.IsNull(log.Body);
        Assert.IsNull(log.Resource.SchemaUrl);
        var trace = new TraceEvidence("11111111111111111111111111111111", snapshot);
        Assert.IsEmpty(trace.Logs);
        Assert.HasCount(1, trace.Capture.Logs);
        Assert.IsNull(trace.SessionId);
    }

    [TestMethod]
    public async Task OtlpReceiverMetrics_UnspecifiedData_StaysVisibleButIsNeverAMeasurement()
    {
        await using var receiver = await OtlpReceiver.StartAsync(TestContext.CancellationToken);
        var request = Metrics(new Resource { Attributes = { Pair("qyl.test.id", new AnyValue { StringValue = "partition" }) } },
            new Metric { Name = "unspecified" });
        using var response = await OtlpHttp.PostAsync(receiver.Endpoint, request.ToByteArray(),
            TestContext.CancellationToken, Signals.Metrics);
        response.EnsureSuccessStatusCode();
        var snapshot = receiver.Snapshot();
        var metric = Assert.ContainsSingle(snapshot.Metrics);
        Assert.AreEqual("unspecified", metric.Name);
        Assert.IsInstanceOfType<UnspecifiedMetricData>(metric.Data);
        Assert.IsEmpty(new TraceEvidence("11111111111111111111111111111111", snapshot,
            new MetricPartition("qyl.test.id", "partition")).Metrics);
        Assert.AreEqual(0L, Assert.ContainsSingle(snapshot.Receipts).Accepted);
    }
}