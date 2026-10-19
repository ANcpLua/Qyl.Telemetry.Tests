using System.Diagnostics;
using Google.Protobuf;
using JetBrains.Annotations;
using OpenTelemetry.Proto.Collector.Logs.V1;
using OpenTelemetry.Proto.Collector.Metrics.V1;
using OpenTelemetry.Proto.Collector.Trace.V1;
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
    public async Task Absent_resource_and_scope_metadata_are_not_fabricated()
    {
        await using var receiver = await OtlpReceiver.StartAsync(TestContext.CancellationToken);
        var request = new ExportTraceServiceRequest
        {
            ResourceSpans =
            {
                new ResourceSpans
                {
                    ScopeSpans =
                    {
                        new ScopeSpans
                        {
                            Spans =
                            {
                                new Span
                                {
                                    Name = "minimal", TraceId = Id("11111111111111111111111111111111"),
                                    SpanId = Id("2222222222222222")
                                }
                            }
                        }
                    }
                }
            }
        };
        using var response =
            await ReceiverTests.PostAsync(receiver.Endpoint, request.ToByteArray(), TestContext.CancellationToken);
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
    public async Task Scope_schema_can_exist_without_an_instrumentation_scope_message()
    {
        await using var receiver = await OtlpReceiver.StartAsync(TestContext.CancellationToken);
        var request = RichSpan();
        request.ResourceSpans[0].ScopeSpans[0].Scope = null;
        using var response =
            await ReceiverTests.PostAsync(receiver.Endpoint, request.ToByteArray(), TestContext.CancellationToken);
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
    [DataRow("histogram")]
    [DataRow("exponential")]
    public async Task Optional_distribution_fields_remain_absent_instead_of_becoming_zero(string kind)
    {
        await using var receiver = await OtlpReceiver.StartAsync(TestContext.CancellationToken);
        var metric = MetricWireTests.Fixture(kind);
        if (kind == "histogram")
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

        var request = new ExportMetricsServiceRequest
        {
            ResourceMetrics = { new ResourceMetrics { ScopeMetrics = { new ScopeMetrics { Metrics = { metric } } } } }
        };
        using var response = await ReceiverTests.PostAsync(receiver.Endpoint, request.ToByteArray(),
            TestContext.CancellationToken, "metrics");
        response.EnsureSuccessStatusCode();
        var observed = Assert.ContainsSingle(receiver.Snapshot().Metrics);
        Assert.AreEqual("metric." + kind, observed.Name);
        Assert.IsNull(observed.Resource.SchemaUrl);
        Assert.IsNull(observed.Scope);
        if (kind == "histogram")
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
    public async Task Uncorrelated_log_without_body_or_context_is_kept_in_the_complete_capture()
    {
        await using var receiver = await OtlpReceiver.StartAsync(TestContext.CancellationToken);
        var request = new ExportLogsServiceRequest
        {
            ResourceLogs =
            {
                new ResourceLogs
                {
                    ScopeLogs =
                    {
                        new ScopeLogs
                        {
                            Scope = new InstrumentationScope { Name = "background" },
                            LogRecords =
                            {
                                new OpenTelemetry.Proto.Logs.V1.LogRecord
                                    { EventName = "BackgroundError", SeverityNumber = SeverityNumber.Error }
                            }
                        }
                    }
                }
            }
        };
        using var response = await ReceiverTests.PostAsync(receiver.Endpoint, request.ToByteArray(),
            TestContext.CancellationToken, "logs");
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
    public async Task Unspecified_metric_data_stays_visible_but_is_never_attributed_as_a_measurement()
    {
        await using var receiver = await OtlpReceiver.StartAsync(TestContext.CancellationToken);
        var request = new ExportMetricsServiceRequest
        {
            ResourceMetrics =
            {
                new ResourceMetrics
                {
                    Resource = new Resource
                        { Attributes = { Pair("qyl.test.id", new AnyValue { StringValue = "partition" }) } },
                    ScopeMetrics = { new ScopeMetrics { Metrics = { new Metric { Name = "unspecified" } } } }
                }
            }
        };
        using var response = await ReceiverTests.PostAsync(receiver.Endpoint, request.ToByteArray(),
            TestContext.CancellationToken, "metrics");
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