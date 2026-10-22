using System.Text.Json;
using Google.Protobuf;
using JetBrains.Annotations;
using OpenTelemetry.Proto.Collector.Metrics.V1;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Metrics.V1;
using OpenTelemetry.Proto.Resource.V1;
using static Qyl.Telemetry.Tests.Evidence.OtlpEvidence;

namespace Qyl.Telemetry.IntegrationTests.Receiver;

[TestClass]
public sealed class MetricWireTests
{
    [UsedImplicitly] public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DataRow(MetricShapes.Gauge)]
    [DataRow(MetricShapes.Sum)]
    [DataRow(MetricShapes.Histogram)]
    [DataRow(MetricShapes.Exponential)]
    [DataRow(MetricShapes.Summary)]
    public async Task Every_metric_shape_retains_its_identity_metadata_and_distribution(string shape)
    {
        await using var receiver = await OtlpReceiver.StartAsync(TestContext.CancellationToken);
        var metric = Fixture(shape);
        var request = new ExportMetricsServiceRequest
        {
            ResourceMetrics =
            {
                new ResourceMetrics
                {
                    SchemaUrl = "resource-schema",
                    Resource = new Resource
                        { Attributes = { Pair("service.name", new AnyValue { StringValue = "metric-service" }) } },
                    ScopeMetrics =
                    {
                        new ScopeMetrics
                        {
                            SchemaUrl = "scope-schema",
                            Scope = new InstrumentationScope { Name = "metric-scope", Version = "4.2" },
                            Metrics = { metric }
                        }
                    }
                }
            }
        };
        using var response = await ReceiverTests.PostAsync(receiver.Endpoint, request.ToByteArray(),
            TestContext.CancellationToken, Signals.Metrics);
        response.EnsureSuccessStatusCode();
        var observed = Assert.ContainsSingle(receiver.Snapshot().Metrics);
        Assert.AreEqual("metric." + shape, observed.Name);
        Assert.AreEqual("Exact distribution", observed.Description);
        Assert.AreEqual("ms", observed.Unit);
        Assert.AreEqual("metric-service", observed.Resource.Attributes["service.name"]);
        Assert.AreEqual("resource-schema", observed.Resource.SchemaUrl);
        Assert.AreEqual("metric-scope", observed.Scope?.Name);
        Assert.AreEqual("4.2", observed.Scope?.Version);
        Assert.AreEqual("scope-schema", observed.Scope?.SchemaUrl);
        Assert.AreEqual("original", observed.Metadata["metadata"]);
        CheckShape(shape, observed.Data);
        // The diagnostic archive must contain the distribution, not an empty abstract base object.
        using var archive = JsonSerializer.SerializeToDocument(receiver.Snapshot());
        var archived = archive.RootElement.GetProperty("Metrics")[0].GetProperty("Data");
        Assert.AreEqual(shape, archived.GetProperty("kind").GetString());
        Assert.AreEqual(1, archived.GetProperty("Points").GetArrayLength());
        var payload = archive.RootElement.GetProperty("Requests")[0];
        Assert.AreEqual(Signals.Metrics, payload.GetProperty("Signal").GetString());
        Assert.AreSequenceEqual(request.ToByteArray(), payload.GetProperty("Payload").GetBytesFromBase64());
        var partitioned = new TraceEvidence("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", receiver.Snapshot(),
            new MetricPartition("service.name", "metric-service"));
        var attributed = Assert.ContainsSingle(partitioned.Metrics);
        Assert.AreEqual(MetricCorrelation.ResourcePartition, attributed.Correlation);
        Assert.AreSequenceEqual([0], attributed.PointIndexes);
        Assert.AreEqual("metric." + shape, attributed.Metric.Name);

        Assert.AreEqual(1L, Assert.ContainsSingle(receiver.Snapshot().Receipts).Accepted);
    }

    [TestMethod]
    public async Task Metric_attribution_requires_matching_resource_partition_or_exemplar_point()
    {
        await using var receiver = await OtlpReceiver.StartAsync(TestContext.CancellationToken);
        var request = new ExportMetricsServiceRequest();
        var resource = new ResourceMetrics { ScopeMetrics = { new ScopeMetrics { Metrics = { Fixture(MetricShapes.Gauge) } } } };
        resource.ScopeMetrics[0].Metrics[0].Gauge.DataPoints.Add(new NumberDataPoint { AsInt = 99 });
        request.ResourceMetrics.Add(resource);
        using var response = await ReceiverTests.PostAsync(receiver.Endpoint, request.ToByteArray(),
            TestContext.CancellationToken, Signals.Metrics);
        response.EnsureSuccessStatusCode();
        var snapshot = receiver.Snapshot();
        var unrelated = new TraceEvidence("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", snapshot);
        Assert.IsEmpty(unrelated.Metrics);
        Assert.HasCount(1, unrelated.Capture.Metrics);
        var related = new TraceEvidence("11111111111111111111111111111111", snapshot);
        var evidence = Assert.ContainsSingle(related.Metrics);
        Assert.AreEqual("metric.gauge", evidence.Metric.Name);
        Assert.AreEqual(MetricCorrelation.Exemplar, evidence.Correlation);
        Assert.AreSequenceEqual([0], evidence.PointIndexes);
        Assert.HasCount(2, Assert.IsInstanceOfType<GaugeData>(evidence.Metric.Data).Points);
        var mismatch = new TraceEvidence("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", snapshot,
            new MetricPartition("qyl.test.id", "wrong"));
        Assert.IsEmpty(mismatch.Metrics);
    }

    internal static Metric Fixture(string shape)
    {
        var metric = new Metric
        {
            Name = "metric." + shape, Description = "Exact distribution", Unit = "ms",
            Metadata = { Pair("metadata", new AnyValue { StringValue = "original" }) }
        };
        var exemplar = new Exemplar
        {
            AsDouble = 2.5, TimeUnixNano = 103, TraceId = Id("11111111111111111111111111111111"),
            SpanId = Id("2222222222222222"),
            FilteredAttributes = { Pair("exemplar", new AnyValue { BoolValue = true }) }
        };
        var point = new NumberDataPoint
        {
            AsInt = -9, StartTimeUnixNano = 101, TimeUnixNano = 109, Flags = 0x80000001,
            Attributes = { Pair("point", new AnyValue { StringValue = "exact" }) }, Exemplars = { exemplar }
        };
        switch (shape)
        {
            case MetricShapes.Gauge: metric.Gauge = new Gauge { DataPoints = { point } }; break;
            case MetricShapes.Sum:
                metric.Sum = new Sum
                {
                    AggregationTemporality = AggregationTemporality.Delta, IsMonotonic = false, DataPoints = { point }
                }; break;
            case MetricShapes.Histogram:
                metric.Histogram = new Histogram
                {
                    AggregationTemporality = AggregationTemporality.Cumulative,
                    DataPoints =
                    {
                        new HistogramDataPoint
                        {
                            Count = 3, Sum = 6.5, Min = 1, Max = 4, StartTimeUnixNano = 101, TimeUnixNano = 109,
                            Flags = 3, BucketCounts = { 1, 2 }, ExplicitBounds = { 2 }, Exemplars = { exemplar }
                        }
                    }
                }; break;
            case MetricShapes.Exponential:
                metric.ExponentialHistogram = new ExponentialHistogram
                {
                    AggregationTemporality = AggregationTemporality.Delta,
                    DataPoints =
                    {
                        new ExponentialHistogramDataPoint
                        {
                            Count = 3, Sum = 2, Min = -2, Max = 4, Scale = -1, ZeroCount = 1, ZeroThreshold = 0.5,
                            StartTimeUnixNano = 101, TimeUnixNano = 109, Flags = 5,
                            Positive = new ExponentialHistogramDataPoint.Types.Buckets
                                { Offset = -2, BucketCounts = { 1 } },
                            Negative = new ExponentialHistogramDataPoint.Types.Buckets
                                { Offset = 3, BucketCounts = { 1 } },
                            Exemplars = { exemplar }
                        }
                    }
                }; break;
            case MetricShapes.Summary:
                metric.Summary = new Summary
                {
                    DataPoints =
                    {
                        new SummaryDataPoint
                        {
                            Count = 3, Sum = 6.5, StartTimeUnixNano = 101, TimeUnixNano = 109, Flags = 7,
                            QuantileValues =
                                { new SummaryDataPoint.Types.ValueAtQuantile { Quantile = 0.5, Value = 2.5 } }
                        }
                    }
                }; break;
            default: throw new ArgumentOutOfRangeException(nameof(shape));
        }

        return metric;
    }

    private static void CheckShape(string shape, MetricData observed)
    {
        switch (shape)
        {
            case MetricShapes.Gauge:
                CheckNumber(Assert.ContainsSingle(Assert.IsInstanceOfType<GaugeData>(observed).Points)); break;
            case MetricShapes.Sum:
                var sum = Assert.IsInstanceOfType<SumData>(observed);
                Assert.AreEqual(1, sum.AggregationTemporality);
                Assert.IsFalse(sum.IsMonotonic);
                CheckNumber(Assert.ContainsSingle(sum.Points));
                break;
            case MetricShapes.Histogram:
                var histogram = Assert.IsInstanceOfType<HistogramData>(observed);
                Assert.AreEqual(2, histogram.AggregationTemporality);
                var h = Assert.ContainsSingle(histogram.Points);
                Assert.AreEqual(3UL, h.Count);
                Assert.AreEqual(6.5, h.Sum);
                Assert.AreEqual(1d, h.Min);
                Assert.AreEqual(4d, h.Max);
                Assert.AreSequenceEqual(new ulong[] { 1, 2 }, h.BucketCounts);
                Assert.AreSequenceEqual([2d], h.ExplicitBounds);
                Assert.AreEqual(101UL, h.StartTimeUnixNano);
                Assert.AreEqual(109UL, h.TimeUnixNano);
                Assert.AreEqual(3u, h.Flags);
                CheckExemplar(h.Exemplars);
                break;
            case MetricShapes.Exponential:
                var exponential = Assert.IsInstanceOfType<ExponentialHistogramData>(observed);
                Assert.AreEqual(1, exponential.AggregationTemporality);
                var e = Assert.ContainsSingle(exponential.Points);
                Assert.AreEqual(3UL, e.Count);
                Assert.AreEqual(2d, e.Sum);
                Assert.AreEqual(-2d, e.Min);
                Assert.AreEqual(4d, e.Max);
                Assert.AreEqual(-1, e.Scale);
                Assert.AreEqual(1UL, e.ZeroCount);
                Assert.AreEqual(0.5, e.ZeroThreshold);
                Assert.AreEqual(-2, e.Positive!.Offset);
                Assert.AreEqual(3, e.Negative!.Offset);
                Assert.AreSequenceEqual(new ulong[] { 1 }, e.Positive!.BucketCounts);
                Assert.AreSequenceEqual(new ulong[] { 1 }, e.Negative!.BucketCounts);
                Assert.AreEqual(101UL, e.StartTimeUnixNano);
                Assert.AreEqual(109UL, e.TimeUnixNano);
                Assert.AreEqual(5u, e.Flags);
                CheckExemplar(e.Exemplars);
                break;
            case MetricShapes.Summary:
                var summary = Assert.ContainsSingle(Assert.IsInstanceOfType<SummaryData>(observed).Points);
                Assert.AreEqual(3UL, summary.Count);
                Assert.AreEqual(6.5, summary.Sum);
                Assert.AreEqual(101UL, summary.StartTimeUnixNano);
                Assert.AreEqual(109UL, summary.TimeUnixNano);
                Assert.AreEqual(7u, summary.Flags);
                Assert.AreEqual(new QuantileValue(0.5, 2.5), Assert.ContainsSingle(summary.Quantiles));
                break;
        }
    }

    private static void CheckNumber(NumberPoint point)
    {
        Assert.AreEqual(-9L, point.Value);
        Assert.AreEqual(101UL, point.StartTimeUnixNano);
        Assert.AreEqual(109UL, point.TimeUnixNano);
        Assert.AreEqual(0x80000001u, point.Flags);
        Assert.AreEqual("exact", point.Attributes["point"]);
        CheckExemplar(point.Exemplars);
    }

    private static void CheckExemplar(IReadOnlyList<ExemplarRecord> exemplars)
    {
        var exemplar = Assert.ContainsSingle(exemplars);
        Assert.AreEqual("11111111111111111111111111111111", exemplar.TraceId);
        Assert.AreEqual("2222222222222222", exemplar.SpanId);
        Assert.AreEqual(103UL, exemplar.TimeUnixNano);
        Assert.AreEqual(2.5, exemplar.Value);
        Assert.IsTrue(Assert.IsInstanceOfType<bool>(exemplar.FilteredAttributes["exemplar"]));
    }
}