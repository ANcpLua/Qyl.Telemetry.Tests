using Google.Protobuf;
using OpenTelemetry.Proto.Common.V1;
using ProtoLog = OpenTelemetry.Proto.Logs.V1.LogRecord;
using Qyl.Telemetry.SemanticConventions;

namespace Qyl.Telemetry.UnitTests.Receiver;

[TestClass]
public sealed class DuplicateAttributeTests
{
    private const string Key = "ConnectionId";

    [TestMethod]
    [DataRow(AttributeShape.String)]
    [DataRow(AttributeShape.Integer)]
    [DataRow(AttributeShape.Array)]
    [DataRow(AttributeShape.Bytes)]
    public void Identical_typed_duplicates_are_coalesced_without_mutating_the_wire_evidence(AttributeShape shape)
    {
        var value = Value(shape);
        var input = new ProtoLog
        {
            Attributes = { new KeyValue { Key = Key, Value = value }, new KeyValue { Key = Key, Value = value.Clone() } }
        };
        var original = input.ToByteArray();
        var decoder = new OtlpDecode();
        var observed = decoder.Log(input, new ResourceRecord(new Dictionary<string, object?>()), null);

        Assert.HasCount(1, observed.Attributes);
        Assert.Contains(Key, observed.Attributes.Keys);
        switch (shape)
        {
            case AttributeShape.String:
                Assert.AreEqual("connection-1", observed.Attributes[Key]);
                break;
            case AttributeShape.Integer:
                Assert.AreEqual(1L, observed.Attributes[Key]);
                break;
            case AttributeShape.Array:
                Assert.AreSequenceEqual(["first", "second"], Assert.IsInstanceOfType<IReadOnlyList<object?>>(observed.Attributes[Key]));
                break;
            case AttributeShape.Bytes:
                Assert.AreSequenceEqual<byte>([1, 2], Assert.IsInstanceOfType<byte[]>(observed.Attributes[Key]));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(shape));
        }
        Assert.AreEqual($"Coalesced identical OTLP attribute: {Key}.", Assert.ContainsSingle(decoder.Warnings));
        Assert.AreSequenceEqual(original, input.ToByteArray());
    }

    [TestMethod]
    [DataRow(AttributeShape.String)]
    [DataRow(AttributeShape.Integer)]
    [DataRow(AttributeShape.Array)]
    [DataRow(AttributeShape.Bytes)]
    public void Conflicting_typed_duplicates_remain_invalid(AttributeShape shape)
    {
        var input = new ProtoLog
        {
            Attributes = { new KeyValue { Key = Key, Value = Value(shape) }, new KeyValue { Key = Key, Value = ConflictingValue(shape) } }
        };
        var failure = Assert.ThrowsExactly<InvalidDataException>(() =>
            new OtlpDecode().Log(input, new ResourceRecord(new Dictionary<string, object?>()), null));
        Assert.AreEqual($"Duplicate OTLP attribute: {Key}.", failure.Message);
    }

    private static AnyValue Value(AttributeShape shape) => shape switch
    {
        AttributeShape.String => new AnyValue { StringValue = "connection-1" },
        AttributeShape.Integer => new AnyValue { IntValue = 1 },
        AttributeShape.Array => new AnyValue { ArrayValue = new ArrayValue { Values = { new AnyValue { StringValue = "first" }, new AnyValue { StringValue = "second" } } } },
        AttributeShape.Bytes => new AnyValue { BytesValue = ByteString.CopyFrom([1, 2]) },
        _ => throw new ArgumentOutOfRangeException(nameof(shape))
    };

    private static AnyValue ConflictingValue(AttributeShape shape) => shape switch
    {
        AttributeShape.String => new AnyValue { StringValue = "connection-2" },
        AttributeShape.Integer => new AnyValue { DoubleValue = 1 },
        AttributeShape.Array => new AnyValue { ArrayValue = new ArrayValue { Values = { new AnyValue { StringValue = "second" }, new AnyValue { StringValue = "first" } } } },
        AttributeShape.Bytes => new AnyValue { BytesValue = ByteString.CopyFrom([2, 1]) },
        _ => throw new ArgumentOutOfRangeException(nameof(shape))
    };

    public enum AttributeShape { String, Integer, Array, Bytes }
}
