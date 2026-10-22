using Qyl.Api.Contracts.Common;
using ContractAttribute = Qyl.Api.Contracts.Common.Attribute;

namespace Qyl.Telemetry.Tests;

[JetBrains.Annotations.PublicAPI]
public static class CapturedAttributeAssertions
{
    extension(Assert)
    {
        public static void HasCapturedValues(IReadOnlyList<ContractAttribute> attributes, string key,
            params string[] expected)
        {
            ArgumentNullException.ThrowIfNull(attributes);
            ArgumentException.ThrowIfNullOrWhiteSpace(key);
            ArgumentNullException.ThrowIfNull(expected);
            var matches = attributes.Where(attribute => attribute.Key == key).ToArray();
            Assert.HasCount(1, matches, $"Expected one captured attribute '{key}'.");
            var values = Assert.IsInstanceOfType<AttributeValue.ArrayValue>(matches[0].Value,
                $"Captured attribute '{key}' must be an array.").Value;
            var actual = values.Select(value => Assert.IsInstanceOfType<AttributeValue.StringValue>(value,
                $"Captured attribute '{key}' must contain strings.").Value).ToArray();
            Assert.AreSequenceEqual(expected, actual, $"Captured attribute '{key}'.");
        }
    }
}
