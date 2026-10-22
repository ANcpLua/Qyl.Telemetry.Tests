using Google.Protobuf;
using JetBrains.Annotations;
using OpenTelemetry.Proto.Collector.Logs.V1;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Logs.V1;
using OpenTelemetry.Proto.Resource.V1;
using static Qyl.Telemetry.Tests.Evidence.OtlpEvidence;
using ProtoLog = OpenTelemetry.Proto.Logs.V1.LogRecord;

namespace Qyl.Telemetry.IntegrationTests.Receiver;

[TestClass]
public sealed class LogWireTests
{
    private const string TextBody = "text";
    private const string IntegerBody = "integer";
    private const string DoubleBody = "double";
    private const string BooleanBody = "boolean";
    private const string BytesBody = "bytes";
    private const string ArrayBody = "array";
    private const string MapBody = "map";
    private const string EmptyBody = "empty";

    [UsedImplicitly] public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DataRow(TextBody)]
    [DataRow(IntegerBody)]
    [DataRow(DoubleBody)]
    [DataRow(BooleanBody)]
    [DataRow(BytesBody)]
    [DataRow(ArrayBody)]
    [DataRow(MapBody)]
    [DataRow(EmptyBody)]
    public async Task OtlpReceiverLogs_EveryBodyTypeAndMetadata_SurviveTheWire(string bodyKind)
    {
        await using var receiver = await OtlpReceiver.StartAsync(TestContext.CancellationToken);
        var log = new ProtoLog
        {
            EventName = "OrderFailed", TimeUnixNano = 101, ObservedTimeUnixNano = 102,
            SeverityNumber = SeverityNumber.Error2,
            SeverityText = "ERROR-DETAIL", Flags = 0x80000001, DroppedAttributesCount = 9,
            TraceId = Id("11111111111111111111111111111111"), SpanId = Id("2222222222222222"),
            Body = Body(bodyKind),
            Attributes =
            {
                Pair("event.name", new AnyValue { StringValue = "attribute-event" }),
                Pair("exception.message", new AnyValue { StringValue = "keep this failure" }),
                Pair("exception.stacktrace", new AnyValue { StringValue = "exact stack" })
            }
        };
        var request = new ExportLogsServiceRequest
        {
            ResourceLogs =
            {
                new ResourceLogs
                {
                    SchemaUrl = "log-resource-schema",
                    Resource = new Resource
                        { Attributes = { Pair("service.name", new AnyValue { StringValue = "log-service" }) } },
                    ScopeLogs =
                    {
                        new ScopeLogs
                        {
                            SchemaUrl = "log-scope-schema",
                            Scope = new InstrumentationScope { Name = "log-scope", Version = "2.1" },
                            LogRecords = { log }
                        }
                    }
                }
            }
        };
        using var response = await OtlpHttp.PostAsync(receiver.Endpoint, request.ToByteArray(),
            TestContext.CancellationToken, Signals.Logs);
        response.EnsureSuccessStatusCode();
        var observed = Assert.ContainsSingle(receiver.Snapshot().Logs);
        Assert.AreEqual("OrderFailed", observed.EventName);
        Assert.AreEqual("attribute-event", observed.Attributes["event.name"]);
        Assert.AreEqual("11111111111111111111111111111111", observed.TraceId);
        Assert.AreEqual("2222222222222222", observed.SpanId);
        Assert.AreEqual(101UL, observed.TimeUnixNano);
        Assert.AreEqual(102UL, observed.ObservedTimeUnixNano);
        Assert.AreEqual(18, observed.SeverityNumber);
        Assert.AreEqual("ERROR-DETAIL", observed.SeverityText);
        Assert.AreEqual(0x80000001u, observed.Flags);
        Assert.AreEqual(9u, observed.DroppedAttributesCount);
        Assert.AreEqual("log-service", observed.Resource.Attributes["service.name"]);
        Assert.AreEqual("log-resource-schema", observed.Resource.SchemaUrl);
        Assert.AreEqual("log-scope", observed.Scope?.Name);
        Assert.AreEqual("2.1", observed.Scope?.Version);
        Assert.AreEqual("log-scope-schema", observed.Scope?.SchemaUrl);
        Assert.AreEqual("keep this failure", observed.Attributes["exception.message"]);
        Assert.AreEqual("exact stack", observed.Attributes["exception.stacktrace"]);
        CheckBody(bodyKind, observed.Body);
    }

    private static AnyValue Body(string kind)
    {
        return kind switch
        {
            TextBody => new AnyValue { StringValue = "message" }, IntegerBody => new AnyValue { IntValue = -42 },
            DoubleBody => new AnyValue { DoubleValue = 0.25 }, BooleanBody => new AnyValue { BoolValue = true },
            BytesBody => new AnyValue { BytesValue = ByteString.CopyFrom([0, 255]) },
            ArrayBody => new AnyValue
            {
                ArrayValue = new ArrayValue
                    { Values = { new AnyValue { StringValue = "item" }, new AnyValue { IntValue = 7 } } }
            },
            MapBody => new AnyValue
                { KvlistValue = new KeyValueList { Values = { Pair("key", new AnyValue { BoolValue = false }) } } },
            EmptyBody => new AnyValue(), _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
    }

    private static void CheckBody(string kind, object? body)
    {
        switch (kind)
        {
            case TextBody: Assert.AreEqual("message", body); break;
            case IntegerBody: Assert.AreEqual(-42L, body); break;
            case DoubleBody: Assert.AreEqual(0.25, body); break;
            case BooleanBody: Assert.IsTrue(Assert.IsInstanceOfType<bool>(body)); break;
            case BytesBody: Assert.AreSequenceEqual(new byte[] { 0, 255 }, Assert.IsInstanceOfType<byte[]>(body)); break;
            case ArrayBody:
                Assert.AreSequenceEqual(["item", 7L],
                    Assert.IsInstanceOfType<IReadOnlyList<object?>>(body)); break;
            case MapBody:
                Assert.IsFalse(
                    Assert.IsInstanceOfType<bool>(
                        Assert.IsInstanceOfType<IReadOnlyDictionary<string, object?>>(body)["key"])); break;
            case EmptyBody: Assert.IsNull(body); break;
        }
    }
}