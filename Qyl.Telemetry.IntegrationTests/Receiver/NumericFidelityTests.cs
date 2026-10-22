using System.Text.Json;
using Google.Protobuf;
using JetBrains.Annotations;
using OpenTelemetry.Proto.Collector.Metrics.V1;
using OpenTelemetry.Proto.Metrics.V1;

namespace Qyl.Telemetry.IntegrationTests.Receiver;

[TestClass]
public sealed class NumericFidelityTests
{
    [UsedImplicitly] public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DataRow(9007199254740993L)]
    [DataRow(long.MinValue)]
    [DataRow(long.MaxValue)]
    public async Task Signed_integer_points_and_exemplars_retain_all_64_bits(long expected)
    {
        var point = new NumberDataPoint { AsInt = expected, Exemplars = { new Exemplar { AsInt = expected } } };
        var metrics = await CaptureAsync(
            new Metric { Name = "integer.gauge", Gauge = new Gauge { DataPoints = { point } } },
            new Metric
            {
                Name = "integer.sum",
                Sum = new Sum
                    { DataPoints = { point.Clone() }, AggregationTemporality = AggregationTemporality.Cumulative }
            });
        Assert.HasCount(2, metrics);
        Assert.AreEqual("integer.gauge", metrics[0].Name);
        Assert.AreEqual("integer.sum", metrics[1].Name);
        var gauge = Assert.ContainsSingle(Assert.IsInstanceOfType<GaugeData>(metrics[0].Data).Points);
        var sum = Assert.ContainsSingle(Assert.IsInstanceOfType<SumData>(metrics[1].Data).Points);
        foreach (var captured in new[] { gauge, sum })
        {
            Assert.AreEqual(expected, Assert.IsInstanceOfType<long>(captured.Value));
            Assert.AreEqual(expected, Assert.IsInstanceOfType<long>(Assert.ContainsSingle(captured.Exemplars).Value));
            using var archive = JsonSerializer.SerializeToDocument(captured);
            Assert.AreEqual(expected, archive.RootElement.GetProperty("Value").GetInt64());
            Assert.AreEqual(expected, archive.RootElement.GetProperty("Exemplars")[0].GetProperty("Value").GetInt64());
        }
    }

    [TestMethod]
    [DataRow("nan", double.NaN)]
    [DataRow("positive-infinity", double.PositiveInfinity)]
    [DataRow("negative-infinity", double.NegativeInfinity)]
    [DataRow("negative-zero", -0.0)]
    public async Task Named_non_finite_and_signed_zero_doubles_keep_their_wire_bits(string name, double expected)
    {
        var metrics = await CaptureAsync(new Metric
        {
            Name = name, Gauge = new Gauge
            {
                DataPoints =
                {
                    new NumberDataPoint { AsDouble = expected, Exemplars = { new Exemplar { AsDouble = expected } } }
                }
            }
        });
        var metric = Assert.ContainsSingle(metrics);
        Assert.AreEqual(name, metric.Name);
        var point = Assert.ContainsSingle(Assert.IsInstanceOfType<GaugeData>(metric.Data).Points);
        var value = Assert.IsInstanceOfType<double>(point.Value);
        var exemplar = Assert.IsInstanceOfType<double>(Assert.ContainsSingle(point.Exemplars).Value);
        Assert.AreEqual(BitConverter.DoubleToInt64Bits(expected), BitConverter.DoubleToInt64Bits(value));
        Assert.AreEqual(BitConverter.DoubleToInt64Bits(expected), BitConverter.DoubleToInt64Bits(exemplar));
    }

    [TestMethod]
    public async Task Unsigned_counts_and_bucket_counts_preserve_uint64_maximum()
    {
        var metrics = await CaptureAsync(
            new Metric
            {
                Name = "histogram", Histogram = new Histogram
                {
                    DataPoints =
                        { new HistogramDataPoint { Count = ulong.MaxValue, BucketCounts = { ulong.MaxValue } } }
                }
            },
            new Metric
            {
                Name = "exponential-zero", ExponentialHistogram = new ExponentialHistogram
                {
                    DataPoints =
                        { new ExponentialHistogramDataPoint { Count = ulong.MaxValue, ZeroCount = ulong.MaxValue } }
                }
            },
            new Metric
            {
                Name = "exponential-positive", ExponentialHistogram = new ExponentialHistogram
                {
                    DataPoints =
                    {
                        new ExponentialHistogramDataPoint
                        {
                            Count = ulong.MaxValue,
                            Positive = new ExponentialHistogramDataPoint.Types.Buckets
                                { BucketCounts = { ulong.MaxValue } }
                        }
                    }
                }
            },
            new Metric
            {
                Name = "summary",
                Summary = new Summary { DataPoints = { new SummaryDataPoint { Count = ulong.MaxValue } } }
            });
        Assert.AreSequenceEqual(["histogram", "exponential-zero", "exponential-positive", "summary"],
            metrics.Select(m => m.Name));
        var histogram = Assert.ContainsSingle(Assert.IsInstanceOfType<HistogramData>(metrics[0].Data).Points);
        Assert.AreEqual(ulong.MaxValue, histogram.Count);
        Assert.AreEqual(ulong.MaxValue, Assert.ContainsSingle(histogram.BucketCounts));
        var zero = Assert.ContainsSingle(Assert.IsInstanceOfType<ExponentialHistogramData>(metrics[1].Data).Points);
        Assert.AreEqual(ulong.MaxValue, zero.Count);
        Assert.AreEqual(ulong.MaxValue, zero.ZeroCount);
        var positive = Assert.ContainsSingle(Assert.IsInstanceOfType<ExponentialHistogramData>(metrics[2].Data).Points);
        Assert.AreEqual(ulong.MaxValue, positive.Count);
        Assert.IsNotNull(positive.Positive);
        Assert.AreEqual(ulong.MaxValue, Assert.ContainsSingle(positive.Positive.BucketCounts));
        Assert.AreEqual(ulong.MaxValue,
            Assert.ContainsSingle(Assert.IsInstanceOfType<SummaryData>(metrics[3].Data).Points).Count);
    }

    private async Task<IReadOnlyList<MetricRecord>> CaptureAsync(params Metric[] metrics)
    {
        await using var receiver = await OtlpReceiver.StartAsync(TestContext.CancellationToken);
        var scope = new ScopeMetrics();
        scope.Metrics.AddRange(metrics);
        var request = new ExportMetricsServiceRequest
            { ResourceMetrics = { new ResourceMetrics { ScopeMetrics = { scope } } } };
        using var response = await ReceiverTests.PostAsync(receiver.Endpoint, request.ToByteArray(),
            TestContext.CancellationToken, Signals.Metrics);
        response.EnsureSuccessStatusCode();
        var receipt = Assert.ContainsSingle(receiver.Snapshot().Receipts);
        Assert.AreEqual(0L, receipt.Rejected);
        Assert.AreEqual(metrics.LongLength, receipt.Accepted);
        return receiver.Snapshot().Metrics;
    }
}