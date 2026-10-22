using JetBrains.Annotations;
using Qyl.Telemetry.SemanticConventions;

namespace Qyl.Telemetry.Tests;

[PublicAPI]
public sealed record Call(SpanRecord Client, SpanRecord Server)
{
    public string? From => Client.Service;
    public string? To => Server.Service;
}

[PublicAPI]
public sealed record MetricPartition(string ResourceAttribute, string Value)
{
    /// <summary>The resource attribute that carries a capture's trace id as its metric partition.</summary>
    internal const string TestIdAttribute = "qyl.test.id";
}

[PublicAPI]
public enum MetricCorrelation
{
    ResourcePartition,
    Exemplar
}

[PublicAPI]
public sealed record MetricEvidence(
    MetricRecord Metric,
    MetricCorrelation Correlation,
    IReadOnlyList<int> PointIndexes);

[PublicAPI]
public sealed class TraceEvidence
{
    public TraceEvidence(string traceId, TelemetrySnapshot capture, MetricPartition? partition = null)
    {
        ArgumentNullException.ThrowIfNull(traceId);
        ArgumentNullException.ThrowIfNull(capture);
        TraceId = traceId;
        Capture = capture;
        Spans = capture.Spans.Where(s => s.TraceId == traceId).AsReadOnlySnapshot();
        Logs = capture.Logs.Where(l => l.TraceId == traceId).AsReadOnlySnapshot();
        Services = Spans.Select(s => s.Service).OfType<string>().Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal).AsReadOnlySnapshot();
        SessionIds = Spans.SelectMany(s => new[]
            {
                s.GetString("session.id"),
                s.Resource.Attributes.TryGetValue("session.id", out var session) ? session as string : null
            })
            .Concat(Logs.SelectMany(l => new[]
            {
                l.Attributes.TryGetValue("session.id", out var session) ? session as string : null,
                l.Resource.Attributes.TryGetValue("session.id", out var resourceSession)
                    ? resourceSession as string
                    : null
            }))
            .OfType<string>().Distinct(StringComparer.Ordinal).AsReadOnlySnapshot();
        var byId = Spans.DistinctBy(s => s.SpanId, StringComparer.Ordinal).ToDictionary(s => s.SpanId, StringComparer.Ordinal);
        // One server span is one call; repeated exports remain in Spans and Capture.
        Calls = byId.Values.Where(s =>
                s is { Kind: SpanKind.Server, ParentSpanId: { } parent } && byId.TryGetValue(parent, out var client)
                                                                         && client.Kind == SpanKind.Client &&
                                                                         client.Service != s.Service)
            .Select(s => new Call(byId[s.ParentSpanId!], s)).AsReadOnlySnapshot();
        Metrics = capture.Metrics.Select(m => Correlate(m, traceId, partition))
            .OfType<MetricEvidence>().AsReadOnlySnapshot();
    }

    public string TraceId { get; }
    public string? SessionId => SessionIds.Count == 1 ? SessionIds[0] : null;
    public IReadOnlyList<string> SessionIds { get; }
    public TelemetrySnapshot Capture { get; }
    public IReadOnlyList<SpanRecord> Spans { get; }
    public IReadOnlyList<LogRecord> Logs { get; }
    public IReadOnlyList<MetricEvidence> Metrics { get; }
    public IReadOnlyList<string> Services { get; }
    public IReadOnlyList<Call> Calls { get; }

    public IReadOnlyList<StoreIdentity> StoresOf(string service)
    {
        return Spans
            .Where(s => s.Service == service && s.GetString("db.system.name") is not null)
            .Select(s => new StoreIdentity(s.GetString("db.system.name"), s.GetString("server.address"),
                s.GetInt64("server.port"), s.GetString("db.namespace")))
            .Distinct().AsReadOnlySnapshot();
    }

    public ConformanceReport Conformance<TKind>(SpanDefinition<TKind> definition, Func<SpanRecord, bool> select)
        where TKind : struct, ISpanKind
    {
        return SpanConformance.Check(Spans, definition, select);
    }

    private static MetricEvidence? Correlate(MetricRecord metric, string traceId, MetricPartition? partition)
    {
        var points = metric.Data.Points().Select(point => point.Exemplars).ToArray();
        if (partition is not null &&
            metric.Resource.Attributes.TryGetValue(partition.ResourceAttribute, out var value) &&
            value is string text && text == partition.Value)
            return points.Length == 0
                ? null
                : new MetricEvidence(metric, MetricCorrelation.ResourcePartition,
                    Enumerable.Range(0, points.Length).AsReadOnlySnapshot());
        var matching = points.Index().Where(p => p.Item.Any(e => e.TraceId == traceId)).Select(p => p.Index).ToArray();
        return matching.Length == 0
            ? null
            : new MetricEvidence(metric, MetricCorrelation.Exemplar, matching.AsReadOnlySnapshot());
    }
}

[PublicAPI]
public sealed record ConformanceIssue(string? SpanId, string Attribute, string Message);

[PublicAPI]
public sealed record ConformanceReport(
    string DefinitionId,
    int SpansChecked,
    IReadOnlyList<ConformanceIssue> Issues,
    IReadOnlyList<string> UnevaluatedConditions)
{
    public bool IsConformant => SpansChecked > 0 && Issues.Count == 0 && UnevaluatedConditions.Count == 0;
}

internal static class SpanConformance
{
    internal static ConformanceReport Check<TKind>(IReadOnlyList<SpanRecord> spans, SpanDefinition<TKind> definition,
        Func<SpanRecord, bool> select) where TKind : struct, ISpanKind
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(select);
        var attributes = definition.Attributes.Select(attribute => (attribute,
            requirement: attribute.RequirementLevel switch
            {
                RequirementLevel.Required => (Required: true, Conditional: false),
                RequirementLevel.ConditionallyRequired => (Required: false, Conditional: true),
                RequirementLevel.Unspecified or RequirementLevel.Recommended or RequirementLevel.OptIn => (
                    Required: false, Conditional: false),
                _ => throw new ArgumentOutOfRangeException(nameof(definition), attribute.RequirementLevel,
                    $"Attribute '{attribute.Name}' has an unsupported requirement level.")
            })).ToArray();
        var selected = spans.Where(select).ToArray();
        var issues = new List<ConformanceIssue>();
        var unevaluated = new List<string>();
        if (selected.Length == 0)
            issues.Add(new ConformanceIssue(null, "", "No span matched the explicit conformance selection."));
        foreach (var span in selected)
        {
            if (!span.Kind.ToString().Equals(definition.SpanKind, StringComparison.OrdinalIgnoreCase))
                issues.Add(new ConformanceIssue(span.SpanId, "span.kind",
                    $"Expected {definition.SpanKind}, observed {span.Kind}."));
            foreach (var (attribute, requirement) in attributes)
            {
                var present = span.Attributes.TryGetValue(attribute.Name, out var value);
                if (!present && requirement.Required)
                    issues.Add(new ConformanceIssue(span.SpanId, attribute.Name, "Required attribute is absent."));
                if (!present && requirement.Conditional)
                    unevaluated.Add($"{span.SpanId}:{attribute.Name}: {attribute.Condition}");
                if (!present) continue;
                if (attribute.IsDeprecated)
                    issues.Add(new ConformanceIssue(span.SpanId, attribute.Name,
                        "The selected definition marks this attribute deprecated."));
                if (!TypeMatches(attribute.Type, value))
                    issues.Add(new ConformanceIssue(span.SpanId, attribute.Name, $"Expected {attribute.Type}."));
            }
        }

        return new ConformanceReport(definition.Id, selected.Length, issues.AsReadOnly(), unevaluated.AsReadOnly());
    }

    private static bool TypeMatches(string type, object? value) => type == "enum"
        ? value is string or long
        : RegistryConformance.Matches(type switch
        {
            "string" => RegistryAttributeValueKind.String,
            "int" => RegistryAttributeValueKind.Integer,
            "double" => RegistryAttributeValueKind.Double,
            "boolean" => RegistryAttributeValueKind.Boolean,
            "string[]" => RegistryAttributeValueKind.StringArray,
            "int[]" => RegistryAttributeValueKind.IntegerArray,
            "double[]" => RegistryAttributeValueKind.DoubleArray,
            "boolean[]" => RegistryAttributeValueKind.BooleanArray,
            "any" or "template" => RegistryAttributeValueKind.Any,
            _ => RegistryAttributeValueKind.Unknown
        }, value);
}