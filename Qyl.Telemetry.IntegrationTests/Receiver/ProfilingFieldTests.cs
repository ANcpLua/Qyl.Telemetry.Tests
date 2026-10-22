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
using ProtoLog = OpenTelemetry.Proto.Logs.V1.LogRecord;

namespace Qyl.Telemetry.IntegrationTests.Receiver;

[TestClass]
public sealed class ProfilingFieldTests
{
    [UsedImplicitly] public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DataRow(Signals.Traces)]
    [DataRow(Signals.Logs)]
    [DataRow(Signals.Metrics)]
    public async Task Receiver_ProfilingStringTableValueOnSignal_IsEmptyWithAWarningNotLeakedToTheNextExport(string signal)
    {
        await using var receiver = await OtlpReceiver.StartAsync(TestContext.CancellationToken);
        var resource = new Resource
        {
            Attributes =
            {
                new KeyValue { Key = "profile.value", Value = new AnyValue { StringValueStrindex = 7 } },
                new KeyValue
                {
                    Key = "nested", Value = new AnyValue
                    {
                        KvlistValue = new KeyValueList
                        {
                            Values = { new KeyValue { Key = "array", Value = new AnyValue
                            {
                                ArrayValue = new ArrayValue { Values = { new AnyValue { StringValueStrindex = 8 } } }
                            } } }
                        }
                    }
                },
                new KeyValue { Key = "unaffected", Value = new AnyValue { IntValue = 42 } }
            }
        };
        var bytes = Request(signal, resource).ToByteArray();
        using var response = await ReceiverTests.PostAsync(receiver.Endpoint, bytes, TestContext.CancellationToken, signal);
        response.EnsureSuccessStatusCode();
        var snapshot = receiver.Snapshot();
        var receipt = Assert.ContainsSingle(snapshot.Receipts);
        Assert.AreEqual(1L, receipt.Accepted);
        Assert.AreEqual(0L, receipt.Rejected);
        Assert.IsNull(receipt.Error);
        Assert.HasCount(2, receipt.Warnings);
        Assert.Contains("StringValueStrindex 7", receipt.Warnings[0]);
        Assert.Contains("StringValueStrindex 8", receipt.Warnings[1]);
        Assert.AreSequenceEqual(bytes, Assert.ContainsSingle(snapshot.Requests).Payload.ToArray());
        var observed = signal switch
        {
            Signals.Traces => Assert.ContainsSingle(snapshot.Spans).Resource,
            Signals.Logs => Assert.ContainsSingle(snapshot.Logs).Resource,
            _ => Assert.ContainsSingle(snapshot.Metrics).Resource
        };
        Assert.AreEqual(string.Empty, observed.Attributes["profile.value"]);
        Assert.AreEqual(42L, observed.Attributes["unaffected"]);
        var map = Assert.IsInstanceOfType<IReadOnlyDictionary<string, object?>>(observed.Attributes["nested"]);
        var array = Assert.IsInstanceOfType<IReadOnlyList<object?>>(map["array"]);
        Assert.AreEqual(string.Empty, Assert.ContainsSingle(array));
        var reply = await response.Content.ReadAsByteArrayAsync(TestContext.CancellationToken);
        long rejected;
        string warning;
        switch (signal)
        {
            case Signals.Traces:
                var traces = ExportTraceServiceResponse.Parser.ParseFrom(reply).PartialSuccess;
                rejected = traces.RejectedSpans;
                warning = traces.ErrorMessage;
                break;
            case Signals.Logs:
                var logs = ExportLogsServiceResponse.Parser.ParseFrom(reply).PartialSuccess;
                rejected = logs.RejectedLogRecords;
                warning = logs.ErrorMessage;
                break;
            default:
                var metrics = ExportMetricsServiceResponse.Parser.ParseFrom(reply).PartialSuccess;
                rejected = metrics.RejectedDataPoints;
                warning = metrics.ErrorMessage;
                break;
        }
        Assert.AreEqual(0L, rejected);
        Assert.AreEqual("application/x-protobuf", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("StringValueStrindex 7", warning);
        Assert.Contains("StringValueStrindex 8", warning);
        using var nextExportWithItsOwnDecoder = await ReceiverTests.PostAsync(receiver.Endpoint, [], TestContext.CancellationToken, signal);
        nextExportWithItsOwnDecoder.EnsureSuccessStatusCode();
        Assert.IsEmpty(receiver.Snapshot().Receipts[1].Warnings);
        Assert.HasCount(1, snapshot.Receipts);
    }

    [TestMethod]
    public async Task Receiver_UnknownProfileKeys_DoNotReplaceLiteralAttributes()
    {
        await using var receiver = await OtlpReceiver.StartAsync(TestContext.CancellationToken);
        var resource = new Resource
        {
            Attributes =
            {
                new KeyValue { KeyStrindex = 3, Value = new AnyValue { StringValue = "unknown key" } },
                new KeyValue { Key = "known", KeyStrindex = 4, Value = new AnyValue { StringValue = "literal" } }
            }
        };
        using var response = await ReceiverTests.PostAsync(receiver.Endpoint, Request(Signals.Logs, resource).ToByteArray(),
            TestContext.CancellationToken, Signals.Logs);
        response.EnsureSuccessStatusCode();
        var snapshot = receiver.Snapshot();
        var log = Assert.ContainsSingle(snapshot.Logs);
        Assert.HasCount(1, log.Resource.Attributes);
        Assert.AreEqual("literal", log.Resource.Attributes["known"]);
        Assert.AreEqual(42L, log.Body);
        var receipt = Assert.ContainsSingle(snapshot.Receipts);
        Assert.AreEqual(0L, receipt.Rejected);
        Assert.HasCount(2, receipt.Warnings);
        Assert.Contains("KeyStrindex 3", receipt.Warnings[0]);
        Assert.Contains("KeyStrindex 4", receipt.Warnings[1]);
    }

    private static IMessage Request(string signal, Resource resource) => signal switch
    {
        Signals.Traces => new ExportTraceServiceRequest
        {
            ResourceSpans = { new ResourceSpans { Resource = resource, ScopeSpans = { new ScopeSpans
            {
                Spans = { new Span
                {
                    TraceId = ByteString.CopyFrom(Convert.FromHexString("11111111111111111111111111111111")),
                    SpanId = ByteString.CopyFrom(Convert.FromHexString("2222222222222222")), Name = "preserved"
                } }
            } } } }
        },
        Signals.Logs => new ExportLogsServiceRequest
        {
            ResourceLogs = { new ResourceLogs { Resource = resource,
                ScopeLogs = { new ScopeLogs { LogRecords = { new ProtoLog { Body = new AnyValue { IntValue = 42 } } } } } } }
        },
        Signals.Metrics => new ExportMetricsServiceRequest
        {
            ResourceMetrics = { new ResourceMetrics { Resource = resource,
                ScopeMetrics = { new ScopeMetrics { Metrics = { MetricWireTests.Fixture(MetricShapes.Gauge) } } } } }
        },
        _ => throw new ArgumentOutOfRangeException(nameof(signal))
    };
}
