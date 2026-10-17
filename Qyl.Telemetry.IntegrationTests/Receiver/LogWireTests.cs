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
    [UsedImplicitly] public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DataRow("text")]
    [DataRow("integer")]
    [DataRow("double")]
    [DataRow("boolean")]
    [DataRow("bytes")]
    [DataRow("array")]
    [DataRow("map")]
    [DataRow("empty")]
    public async Task Log_body_types_and_all_log_metadata_survive_the_wire(string bodyKind)
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
        using var response = await ReceiverTests.PostAsync(receiver.Endpoint, request.ToByteArray(),
            TestContext.CancellationToken, "logs");
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
            "text" => new AnyValue { StringValue = "message" }, "integer" => new AnyValue { IntValue = -42 },
            "double" => new AnyValue { DoubleValue = 0.25 }, "boolean" => new AnyValue { BoolValue = true },
            "bytes" => new AnyValue { BytesValue = ByteString.CopyFrom([0, 255]) },
            "array" => new AnyValue
            {
                ArrayValue = new ArrayValue
                    { Values = { new AnyValue { StringValue = "item" }, new AnyValue { IntValue = 7 } } }
            },
            "map" => new AnyValue
                { KvlistValue = new KeyValueList { Values = { Pair("key", new AnyValue { BoolValue = false }) } } },
            "empty" => new AnyValue(), _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
    }

    private static void CheckBody(string kind, object? body)
    {
        switch (kind)
        {
            case "text": Assert.AreEqual("message", body); break;
            case "integer": Assert.AreEqual(-42L, body); break;
            case "double": Assert.AreEqual(0.25, body); break;
            case "boolean": Assert.IsTrue(Assert.IsInstanceOfType<bool>(body)); break;
            case "bytes": Assert.AreSequenceEqual(new byte[] { 0, 255 }, Assert.IsInstanceOfType<byte[]>(body)); break;
            case "array":
                Assert.AreSequenceEqual(["item", 7L],
                    Assert.IsInstanceOfType<IReadOnlyList<object?>>(body)); break;
            case "map":
                Assert.IsFalse(
                    Assert.IsInstanceOfType<bool>(
                        Assert.IsInstanceOfType<IReadOnlyDictionary<string, object?>>(body)["key"])); break;
            case "empty": Assert.IsNull(body); break;
        }
    }
}