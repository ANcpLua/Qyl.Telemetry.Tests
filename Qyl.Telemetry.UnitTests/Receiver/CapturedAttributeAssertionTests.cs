using Qyl.Api.Contracts.Common;
using ContractAttribute = Qyl.Api.Contracts.Common.Attribute;

namespace Qyl.Telemetry.UnitTests.Receiver;

[TestClass]
public sealed class CapturedAttributeAssertionTests
{
    private const string Key = "http.request.header.x-example";

    [TestMethod]
    public void CapturedAttributeAssertion_EmptyValuesDuplicatesAndEmbeddedCommas_ArePreserved()
    {
        string[] expected = ["", "first", "first", "a,b"];
        ContractAttribute[] attributes =
        [
            new() { Key = Key, Value = new AttributeValue.ArrayValue([.. expected.Select(value => new AttributeValue.StringValue(value))]) }
        ];
        Assert.HasCapturedValues(attributes, Key, expected);
    }

    [TestMethod]
    [DataRow(InvalidCapturedArray.Missing)]
    [DataRow(InvalidCapturedArray.DuplicateKey)]
    [DataRow(InvalidCapturedArray.Scalar)]
    [DataRow(InvalidCapturedArray.NonString)]
    [DataRow(InvalidCapturedArray.Reordered)]
    public void CapturedAttributeAssertion_MissingAmbiguousOrChangedEvidence_IsRejected(InvalidCapturedArray violation)
    {
        var valid = new ContractAttribute
        {
            Key = Key,
            Value = new AttributeValue.ArrayValue([new AttributeValue.StringValue("first"), new AttributeValue.StringValue("second")])
        };
        ContractAttribute[] attributes = violation switch
        {
            InvalidCapturedArray.Missing => [],
            InvalidCapturedArray.DuplicateKey => [valid, valid],
            InvalidCapturedArray.Scalar => [new ContractAttribute { Key = Key, Value = new AttributeValue.StringValue("first") }],
            InvalidCapturedArray.NonString => [new ContractAttribute { Key = Key, Value = new AttributeValue.ArrayValue([new AttributeValue.BoolValue(true)]) }],
            InvalidCapturedArray.Reordered => [new ContractAttribute { Key = Key, Value = new AttributeValue.ArrayValue([new AttributeValue.StringValue("second"), new AttributeValue.StringValue("first")]) }],
            _ => throw new ArgumentOutOfRangeException(nameof(violation))
        };
        var failure = Assert.ThrowsExactly<AssertFailedException>(() => Assert.HasCapturedValues(attributes, Key, "first", "second"));
        Assert.Contains(Key, failure.Message);
    }

    public enum InvalidCapturedArray { Missing, DuplicateKey, Scalar, NonString, Reordered }
}
