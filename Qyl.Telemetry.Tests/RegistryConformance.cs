using JetBrains.Annotations;
using Qyl.Telemetry.SemanticConventions;

namespace Qyl.Telemetry.Tests;

[PublicAPI]
public enum RegistryIssueLevel
{
    Improvement,
    Violation
}

[PublicAPI]
public sealed record RegistryIssue(string Location, string Name, RegistryIssueLevel Level, string Message);

[PublicAPI]
public sealed record RegistryReport(
    int RecordsChecked,
    int AttributesChecked,
    int MetricsChecked,
    IReadOnlyList<RegistryIssue> Issues,
    IReadOnlyList<string> CustomAttributes,
    IReadOnlyList<string> CustomMetrics)
{
    public Uri RegistrySchemaUrl { get; } = new(SchemaUrl.Qyl);
    public Uri UpstreamSchemaUrl { get; } = new(SchemaUrl.Current);
    public string RegistrySha256 { get; } = RegistrySnapshot.Sha256;

    public bool IsConformant => RecordsChecked > 0 && Issues.All(issue => issue.Level != RegistryIssueLevel.Violation);
}

/// <summary>Checks observed attribute types and deprecations, and registered metric units and deprecations.</summary>
/// <remarks>
///     Unknown custom names are valid and reported separately. This does not infer required attributes,
///     close enum value sets, assign names to scopes, or infer an SDK instrument from an OTLP aggregation.
///     Signal-specific requirements belong to an explicitly selected definition.
/// </remarks>
[PublicAPI]
public static class RegistryConformance
{
    public static RegistryReport Check(TelemetrySnapshot capture)
    {
        ArgumentNullException.ThrowIfNull(capture);
        var issues = new List<RegistryIssue>();
        var customAttributes = new SortedSet<string>(StringComparer.Ordinal);
        var customMetrics = new SortedSet<string>(StringComparer.Ordinal);
        var attributesChecked = 0;
        var metricsChecked = 0;
        foreach (var (location, attributes) in AttributeSets(capture))
        foreach (var (name, value) in attributes)
        {
            if (!RegistryCatalog.TryGetAttribute(name, out var attribute))
            {
                customAttributes.Add(name);
                continue;
            }

            attributesChecked++;
            if (attribute.Deprecation.IsDeprecated)
                issues.Add(new RegistryIssue(location, name, RegistryIssueLevel.Improvement,
                    "The pinned registry marks this attribute deprecated."));
            if (!Matches(attribute.ValueKind, value))
                issues.Add(new RegistryIssue(location, name, RegistryIssueLevel.Violation,
                    $"Expected {attribute.ValueKind}."));
        }

        for (var i = 0; i < capture.Metrics.Count; i++)
        {
            var observed = capture.Metrics[i];
            if (!RegistryCatalog.TryGetMetric(observed.Name, out var metric))
            {
                customMetrics.Add(observed.Name);
                continue;
            }

            metricsChecked++;
            var location = $"metrics[{i}]";
            if (metric.Deprecation.IsDeprecated)
                issues.Add(new RegistryIssue(location, observed.Name, RegistryIssueLevel.Improvement,
                    "The pinned registry marks this metric deprecated."));
            if (metric.Unit is not null && metric.Unit != observed.Unit)
                issues.Add(new RegistryIssue(location, observed.Name, RegistryIssueLevel.Violation,
                    $"Expected unit '{metric.Unit}', observed '{observed.Unit}'."));
        }

        return new RegistryReport(capture.Spans.Count + capture.Logs.Count + capture.Metrics.Count,
            attributesChecked, metricsChecked, issues.AsReadOnly(), customAttributes.AsReadOnlySnapshot(),
            customMetrics.AsReadOnlySnapshot());
    }

    internal static bool Matches(RegistryAttributeValueKind kind, object? value)
    {
        return kind switch
        {
            RegistryAttributeValueKind.String => value is string,
            RegistryAttributeValueKind.Integer => value is long,
            RegistryAttributeValueKind.Double => value is double,
            RegistryAttributeValueKind.Boolean => value is bool,
            RegistryAttributeValueKind.StringArray => value is IReadOnlyList<object?> strings &&
                                                      strings.All(v => v is string),
            RegistryAttributeValueKind.IntegerArray => value is IReadOnlyList<object?> integers &&
                                                       integers.All(v => v is long),
            RegistryAttributeValueKind.DoubleArray => value is IReadOnlyList<object?> doubles &&
                                                      doubles.All(v => v is double),
            RegistryAttributeValueKind.BooleanArray => value is IReadOnlyList<object?> booleans &&
                                                       booleans.All(v => v is bool),
            RegistryAttributeValueKind.Any => true,
            _ => false
        };
    }

    private static IEnumerable<(string Location, IReadOnlyDictionary<string, object?> Attributes)> AttributeSets(
        TelemetrySnapshot capture)
    {
        for (var i = 0; i < capture.Spans.Count; i++)
        {
            var span = capture.Spans[i];
            var location = $"spans[{i}]";
            foreach (var set in Context(location, span.Resource, span.Scope)) yield return set;
            yield return (location, span.Attributes);
            for (var j = 0; j < span.Events.Count; j++)
                yield return ($"{location}.events[{j}]", span.Events[j].Attributes);
            for (var j = 0; j < span.Links.Count; j++)
                yield return ($"{location}.links[{j}]", span.Links[j].Attributes);
        }

        for (var i = 0; i < capture.Logs.Count; i++)
        {
            var log = capture.Logs[i];
            var location = $"logs[{i}]";
            foreach (var set in Context(location, log.Resource, log.Scope)) yield return set;
            yield return (location, log.Attributes);
        }

        for (var i = 0; i < capture.Metrics.Count; i++)
        {
            var metric = capture.Metrics[i];
            var location = $"metrics[{i}]";
            foreach (var set in Context(location, metric.Resource, metric.Scope)) yield return set;
            var pointIndex = 0;
            foreach (var (attributes, exemplars) in metric.Data.Points())
            {
                var pointLocation = $"{location}.points[{pointIndex++}]";
                yield return (pointLocation, attributes);
                for (var j = 0; j < exemplars.Count; j++)
                    yield return ($"{pointLocation}.exemplars[{j}]", exemplars[j].FilteredAttributes);
            }
        }
    }

    private static IEnumerable<(string Location, IReadOnlyDictionary<string, object?> Attributes)> Context(
        string location, ResourceRecord resource, InstrumentationScopeRecord? scope)
    {
        yield return ($"{location}.resource", resource.Attributes);
        if (scope is not null) yield return ($"{location}.scope", scope.Attributes);
    }
}
