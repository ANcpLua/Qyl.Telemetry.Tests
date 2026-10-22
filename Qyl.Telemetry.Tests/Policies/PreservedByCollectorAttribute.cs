using JetBrains.Annotations;
using Qyl.Api.Contracts.Common;
using Qyl.Telemetry.SemanticConventions;
using ContractAttribute = Qyl.Api.Contracts.Common.Attribute;
using StoredSpan = Qyl.Api.Contracts.OTel.Traces.Span;

namespace Qyl.Telemetry.Tests.Policies;

/// <summary>
///     The Collector stores every captured span of the test's traces as captured: name, parent, kind, status,
///     timestamps, scope, events, and each span and resource attribute that <see cref="RegistryPolicy.IsPersisted" />
///     retains, with its typed value. It keeps no denied attribute and adds none, except a copy of the span's own
///     resource attribute. Exception text is compared exactly, so a scenario that expects sanitization asserts the
///     stored text itself.
/// </summary>
/// <remarks>Requires a <see cref="Collector.TelemetryPipeline" /> root; the pipeline reads the trace back before stopping its Collector.</remarks>
[PublicAPI]
public sealed class PreservedByCollectorAttribute : TelemetryPolicyAttribute
{
    private protected override IEnumerable<string> Violations(TelemetryInvocation invocation)
    {
        foreach (var trace in invocation.Traces)
        {
            if (trace.Stored is not { } stored)
            {
                yield return $"Trace {trace.Evidence.TraceId} was not read back from a Collector. Capture it through a "
                    + "TelemetryPipeline root; TelemetryCapture.TraceAsync has no Collector.";
                continue;
            }

            var storedById = stored.DistinctBy(span => span.SpanId, StringComparer.Ordinal)
                .ToDictionary(span => span.SpanId, StringComparer.Ordinal);
            foreach (var span in trace.Evidence.Spans.DistinctBy(span => span.SpanId))
            {
                if (!storedById.TryGetValue(span.SpanId, out var kept))
                {
                    yield return $"{Describe(span)} span {span.SpanId} was captured but not stored.";
                    continue;
                }

                foreach (var difference in Differences(span, kept)) yield return $"{Describe(span)} {difference}";
            }
        }
    }

    private static IEnumerable<string> Differences(SpanRecord captured, StoredSpan stored)
    {
        if (captured.Name != stored.Name) yield return $"was stored as '{stored.Name}'.";
        if (Parent(captured.ParentSpanId) != Parent(stored.ParentSpanId))
            yield return $"was stored under parent '{stored.ParentSpanId}' instead of '{captured.ParentSpanId}'.";
        if (captured.Kind.ToString() != stored.Kind.ToString()) yield return $"was stored with kind {stored.Kind}.";
        if (captured.Status.ToString() != stored.Status.Code.ToString())
            yield return $"was stored with status {stored.Status.Code} instead of {captured.Status}.";
        if (captured.StartTimeUnixNano != stored.StartTimeUnixNano || captured.EndTimeUnixNano != stored.EndTimeUnixNano)
            yield return $"was stored from {stored.StartTimeUnixNano} to {stored.EndTimeUnixNano} instead of "
                + $"{captured.StartTimeUnixNano} to {captured.EndTimeUnixNano}.";
        if ((captured.Scope?.Name ?? "") != (stored.InstrumentationScope?.ScopeName ?? ""))
            yield return $"was stored with scope '{stored.InstrumentationScope?.ScopeName}'.";
        foreach (var difference in Differences(captured.Attributes, stored.Attributes, captured.Resource.Attributes))
            yield return difference;
        foreach (var difference in Differences(captured.Resource.Attributes, stored.Resource.Attributes, null))
            yield return "resource " + difference;
        foreach (var capturedEvent in captured.Events)
        {
            var storedEvent = stored.Events?.FirstOrDefault(item =>
                item.Name == capturedEvent.Name && item.TimeUnixNano == capturedEvent.TimeUnixNano);
            if (storedEvent is null)
            {
                yield return $"lost event '{capturedEvent.Name}' at {capturedEvent.TimeUnixNano}.";
                continue;
            }

            foreach (var difference in Differences(capturedEvent.Attributes, storedEvent.Attributes, null))
                yield return $"event '{capturedEvent.Name}' {difference}";
        }
    }

    /// <summary>Compares typed attributes; <paramref name="resource" /> holds those the Collector may copy onto the record.</summary>
    private static IEnumerable<string> Differences(IReadOnlyDictionary<string, object?> captured,
        IReadOnlyList<ContractAttribute>? stored, IReadOnlyDictionary<string, object?>? resource)
    {
        var kept = stored ?? [];
        foreach (var (key, count) in kept.CountBy(attribute => attribute.Key, StringComparer.Ordinal).Where(pair => pair.Value > 1))
            yield return $"stored '{key}' {count} times.";
        var values = kept.DistinctBy(attribute => attribute.Key, StringComparer.Ordinal)
            .ToDictionary(attribute => attribute.Key, attribute => Plain(attribute.Value), StringComparer.Ordinal);
        foreach (var (key, value) in captured.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var persisted = RegistryPolicy.IsPersisted(key);
            if (!values.TryGetValue(key, out var storedValue))
            {
                if (persisted) yield return $"lost '{key}' = {TelemetryReport.Format(value)}.";
            }
            else if (!persisted) yield return $"retained '{key}', which the persistence policy denies.";
            else if (!ValueEquals(value, storedValue))
                yield return $"changed '{key}' from {TelemetryReport.Format(value)} to {TelemetryReport.Format(storedValue)}.";
        }

        foreach (var key in values.Keys.Where(key => !captured.ContainsKey(key)).Order(StringComparer.Ordinal))
            if (resource is null || !resource.TryGetValue(key, out var copied) || !ValueEquals(copied, values[key]))
                yield return $"gained '{key}' = {TelemetryReport.Format(values[key])}.";
    }

    private static string Parent(string? id) => string.IsNullOrEmpty(id) || id.All(digit => digit == '0') ? "" : id;

    internal static object? Plain(AttributeValue? value) => value switch
    {
        null => null,
        AttributeValue.StringValue text => text.Value,
        AttributeValue.BoolValue flag => flag.Value,
        AttributeValue.ArrayValue items => items.Value.Select(Plain).ToArray(),
        AttributeValue.ObjectValue { Value: AttributeIntValue number } => number.Value,
        AttributeValue.ObjectValue { Value: AttributeDoubleValue number } => number.Value,
        AttributeValue.ObjectValue { Value: AttributeBytesValue bytes } => bytes.Base64.ToArray(),
        AttributeValue.ObjectValue { Value: AttributeKeyValueListValue map } =>
            map.Values.ToDictionary(pair => pair.Key, pair => Plain(pair.Value), StringComparer.Ordinal),
        _ => value
    };

    private static bool ValueEquals(object? captured, object? stored) => (captured, stored) switch
    {
        (null, null) => true,
        (byte[] left, byte[] right) => left.AsSpan().SequenceEqual(right),
        (IReadOnlyDictionary<string, object?> left, IReadOnlyDictionary<string, object?> right) =>
            left.Count == right.Count && left.All(pair => right.TryGetValue(pair.Key, out var other) && ValueEquals(pair.Value, other)),
        (IReadOnlyList<object?> left, IReadOnlyList<object?> right) =>
            left.Count == right.Count && left.Zip(right).All(pair => ValueEquals(pair.First, pair.Second)),
        _ => Equals(captured, stored)
    };
}
