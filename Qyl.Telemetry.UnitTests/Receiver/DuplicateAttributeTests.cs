using Google.Protobuf;
using OpenTelemetry.Proto.Common.V1;
using ProtoLog = OpenTelemetry.Proto.Logs.V1.LogRecord;
using Qyl.Telemetry.SemanticConventions;
using Qyl.Telemetry.Tests.Evidence;

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
    public void DuplicateAttributes_IdenticalTypedValues_AreCoalescedIntoOneAttribute(AttributeShape shape)
    {
        var (observed, _, _, _) = DecodeIdenticalDuplicates(shape);
        Assert.AreSequenceEqual([Key], observed.Attributes.Keys);
    }

    [TestMethod]
    [DataRow(AttributeShape.String)]
    [DataRow(AttributeShape.Integer)]
    [DataRow(AttributeShape.Array)]
    [DataRow(AttributeShape.Bytes)]
    public void DuplicateAttributes_IdenticalTypedValues_ReportACoalescingWarning(AttributeShape shape)
    {
        var (_, warnings, _, _) = DecodeIdenticalDuplicates(shape);
        Assert.AreEqual($"Coalesced identical OTLP attribute: {Key}.", Assert.ContainsSingle(warnings));
    }

    [TestMethod]
    [DataRow(AttributeShape.String)]
    [DataRow(AttributeShape.Integer)]
    [DataRow(AttributeShape.Array)]
    [DataRow(AttributeShape.Bytes)]
    public void DuplicateAttributes_IdenticalTypedValues_LeaveTheWireMessageUnchanged(AttributeShape shape)
    {
        var (_, _, original, input) = DecodeIdenticalDuplicates(shape);
        Assert.AreSequenceEqual(original, input.ToByteArray());
    }

    [TestMethod]
    public void DuplicateAttributes_IdenticalStrings_KeepTheString()
    {
        var (observed, _, _, _) = DecodeIdenticalDuplicates(AttributeShape.String);
        Assert.AreEqual("connection-1", observed.Attributes[Key]);
    }

    [TestMethod]
    public void DuplicateAttributes_IdenticalIntegers_KeepTheInt64()
    {
        var (observed, _, _, _) = DecodeIdenticalDuplicates(AttributeShape.Integer);
        Assert.AreEqual(1L, observed.Attributes[Key]);
    }

    [TestMethod]
    public void DuplicateAttributes_IdenticalArrays_KeepTheOrderedValues()
    {
        var (observed, _, _, _) = DecodeIdenticalDuplicates(AttributeShape.Array);
        Assert.AreSequenceEqual(["first", "second"], Assert.IsInstanceOfType<IReadOnlyList<object?>>(observed.Attributes[Key]));
    }

    [TestMethod]
    public void DuplicateAttributes_IdenticalBytes_KeepTheBytes()
    {
        var (observed, _, _, _) = DecodeIdenticalDuplicates(AttributeShape.Bytes);
        Assert.AreSequenceEqual<byte>([1, 2], Assert.IsInstanceOfType<byte[]>(observed.Attributes[Key]));
    }

    [TestMethod]
    [DataRow(AttributeShape.String)]
    [DataRow(AttributeShape.Integer)]
    [DataRow(AttributeShape.Array)]
    [DataRow(AttributeShape.Bytes)]
    public void DuplicateAttributes_ConflictingTypedValues_RemainInvalid(AttributeShape shape)
    {
        var input = new ProtoLog
        {
            Attributes = { OtlpEvidence.Pair(Key, Value(shape)), OtlpEvidence.Pair(Key, ConflictingValue(shape)) }
        };
        var failure = Assert.ThrowsExactly<InvalidDataException>(() =>
            new OtlpDecode().Log(input, new ResourceRecord(new Dictionary<string, object?>()), null));
        Assert.AreEqual($"Duplicate OTLP attribute: {Key}.", failure.Message);
    }

    private static (LogRecord Observed, IReadOnlyList<string> Warnings, byte[] Original, ProtoLog Input) DecodeIdenticalDuplicates(AttributeShape shape)
    {
        var value = Value(shape);
        var input = new ProtoLog
        {
            Attributes = { OtlpEvidence.Pair(Key, value), OtlpEvidence.Pair(Key, value.Clone()) }
        };
        var original = input.ToByteArray();
        var decoder = new OtlpDecode();
        var observed = decoder.Log(input, new ResourceRecord(new Dictionary<string, object?>()), null);
        return (observed, [.. decoder.Warnings], original, input);
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
