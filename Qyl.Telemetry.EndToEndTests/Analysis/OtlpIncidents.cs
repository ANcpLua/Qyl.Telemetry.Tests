using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using OpenTelemetry.Proto.Collector.Metrics.V1;
using OpenTelemetry.Proto.Collector.Trace.V1;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Metrics.V1;
using OpenTelemetry.Proto.Resource.V1;
using OpenTelemetry.Proto.Trace.V1;
using Qyl.Telemetry.Tests.Collector;
using Qyl.Telemetry.Tests.Evidence;
using OtlpSpan = OpenTelemetry.Proto.Trace.V1.Span;

namespace Qyl.Telemetry.EndToEndTests.Analysis;

internal readonly record struct SpanKey(string TraceId, string SpanId);

/// <summary>Builds incidents as OTLP protobuf and sends them through the Collector's real ingestion routes.</summary>
internal sealed class OtlpIncidents
{
    internal const ulong Millisecond = TimeSpan.TicksPerMillisecond * TimeSpan.NanosecondsPerTick;
    internal const ulong Second = TimeSpan.TicksPerSecond * TimeSpan.NanosecondsPerTick;
    internal const ulong Minute = TimeSpan.TicksPerMinute * TimeSpan.NanosecondsPerTick;
    private readonly List<ResourceSpans> _spans = [];
    private readonly List<ResourceMetrics> _metrics = [];
    private readonly Dictionary<string, SpanKey> _keys = new(StringComparer.Ordinal);

    internal IReadOnlyDictionary<string, SpanKey> Keys => _keys;

    /// <summary>A deterministic hex identity derived from a scenario key.</summary>
    private static string Id(string key, int length) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..length];

    internal static ulong Nanoseconds(DateTimeOffset time) => (ulong)time.ToUnixTimeMilliseconds() * Millisecond;

    internal static DateTimeOffset Time(ulong nanoseconds) => DateTimeOffset.FromUnixTimeMilliseconds((long)(nanoseconds / Millisecond));

    internal SpanKey Span(string key, string service, string name, ulong at, double durationMs = 20, bool error = false,
        string? session = null, string? parent = null, string? trace = null, IReadOnlyDictionary<string, object>? attributes = null,
        IReadOnlyDictionary<string, object>? resource = null, string? message = null, string? spanId = null)
    {
        var identity = new SpanKey(trace ?? Id(key, 32), spanId ?? Id(key, 16));
        var span = new OtlpSpan
        {
            TraceId = OtlpEvidence.Id(identity.TraceId), SpanId = OtlpEvidence.Id(identity.SpanId), Name = name, Kind = OtlpSpan.Types.SpanKind.Server,
            StartTimeUnixNano = at, EndTimeUnixNano = at + (ulong)(durationMs * Millisecond),
            Status = new Status { Code = error ? Status.Types.StatusCode.Error : Status.Types.StatusCode.Ok, Message = message ?? "" }
        };
        if (parent is not null) span.ParentSpanId = OtlpEvidence.Id(parent);
        span.Attributes.Add(Attributes(attributes));
        if (session is not null) span.Attributes.Add(OtlpEvidence.Pair("session.id", session));
        _spans.Add(new ResourceSpans
        {
            Resource = Resource(service, resource),
            ScopeSpans = { new ScopeSpans { Scope = new InstrumentationScope { Name = "qyl-answer-key" }, Spans = { span } } }
        });
        _keys[key] = identity;
        return identity;
    }

    internal void Metric(string service, Metric metric) => _metrics.Add(new ResourceMetrics
    {
        Resource = Resource(service, null),
        ScopeMetrics = { new ScopeMetrics { Scope = new InstrumentationScope { Name = "qyl-answer-key" }, Metrics = { metric } } }
    });

    private static IEnumerable<KeyValue> Attributes(IReadOnlyDictionary<string, object>? attributes) =>
        (attributes ?? new Dictionary<string, object>()).Select(pair => OtlpEvidence.Pair(pair.Key, pair.Value));

    /// <summary>Sends every pending signal, then waits until the Collector stores every span of <paramref name="key" />'s trace.</summary>
    internal async Task SendAndWaitAsync(CollectorClient client, string key, CancellationToken cancellationToken)
    {
        var traceId = _keys[key].TraceId;
        var spanIds = _keys.Values.Where(identity => identity.TraceId == traceId).Select(identity => identity.SpanId)
            .ToHashSet(StringComparer.Ordinal);
        await SendAsync(client, cancellationToken);
        _ = await client.WaitForSpansAsync(traceId, spanIds, cancellationToken);
    }

    /// <summary>Sends every pending signal and requires each export to be accepted completely.</summary>
    internal async Task SendAsync(CollectorClient client, CancellationToken cancellationToken)
    {
        if (_spans.Count > 0)
        {
            var request = new ExportTraceServiceRequest();
            request.ResourceSpans.Add(_spans);
            var response = ExportTraceServiceResponse.Parser.ParseFrom(await ExportAsync(client, "v1/traces", request, cancellationToken));
            if ((response.PartialSuccess?.RejectedSpans ?? 0) != 0)
                throw new InvalidDataException($"The incident export rejected spans: {response.PartialSuccess?.ErrorMessage}");
        }

        if (_metrics.Count > 0)
        {
            var request = new ExportMetricsServiceRequest();
            request.ResourceMetrics.Add(_metrics);
            var response = ExportMetricsServiceResponse.Parser.ParseFrom(await ExportAsync(client, "v1/metrics", request, cancellationToken));
            if ((response.PartialSuccess?.RejectedDataPoints ?? 0) != 0)
                throw new InvalidDataException($"The incident export rejected metric points: {response.PartialSuccess?.ErrorMessage}");
        }

        _spans.Clear();
        _metrics.Clear();
    }

    private static async Task<byte[]> ExportAsync(CollectorClient client, string route, IMessage request, CancellationToken cancellationToken)
    {
        using var response = await client.ExportAsync(route, request.ToByteArray(), OtlpReceiver.ProtobufMediaType, null, cancellationToken);
        var body = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        return response.IsSuccessStatusCode
            ? body
            : throw new HttpRequestException($"{route} returned {(int)response.StatusCode}: {Encoding.UTF8.GetString(body)}", null, response.StatusCode);
    }

    private static Resource Resource(string service, IReadOnlyDictionary<string, object>? attributes)
    {
        var resource = new Resource { Attributes = { OtlpEvidence.Pair("service.name", service) } };
        resource.Attributes.Add(Attributes(attributes));
        return resource;
    }
}
