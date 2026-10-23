using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Qyl.Api.Contracts;
using Qyl.Api.Contracts.Common.Pagination;
using StoredLogRecord = Qyl.Api.Contracts.OTel.Logs.LogRecord;
using Qyl.Api.Contracts.OTel.Metrics;
using Qyl.Api.Contracts.OTel.Traces;
using StoredSpan = Qyl.Api.Contracts.OTel.Traces.Span;
using StoredTrace = Qyl.Api.Contracts.OTel.Traces.Trace;

namespace Qyl.Telemetry.Tests.Collector;

/// <summary>Reads telemetry persisted by the qyl platform. The caller owns the supplied HTTP client.</summary>
/// <param name="client">A client whose base address is the qyl collector.</param>
/// <param name="timeProvider">The clock that paces each wait and ends it at its deadline.</param>
[JetBrains.Annotations.PublicAPI]
public sealed class CollectorClient(HttpClient client, TimeProvider timeProvider)
{
    internal static TimeSpan WaitDeadline { get; } = TimeSpan.FromSeconds(20);
    internal static TimeSpan PollingInterval { get; } = TimeSpan.FromMilliseconds(100);

    /// <summary>Reads through <paramref name="client" /> on the system clock.</summary>
    /// <param name="client">A client whose base address is the qyl collector.</param>
    public CollectorClient(HttpClient client) : this(client, TimeProvider.System)
    {
    }

    public TimeProvider TimeProvider { get; } = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public async Task<JsonElement> ReadTraceAsync(string traceId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(traceId);
        return ContractJson.Serialize(await ReadAsync<StoredTrace>(TraceRoute(traceId), cancellationToken).ConfigureAwait(false));
    }

    private static string TraceRoute(string traceId) => $"api/v1/traces/{Uri.EscapeDataString(traceId)}";

    public Task<JsonElement> WaitForTraceAsync(string traceId, Func<JsonElement, bool> until,
        CancellationToken cancellationToken = default) =>
        WaitUntilAsync(token => ReadTraceAsync(traceId, token), until, $"trace '{traceId}'", cancellationToken);

    public Task<IReadOnlyList<StoredLogRecord>> WaitForLogsAsync(string traceId,
        Func<IReadOnlyList<StoredLogRecord>, bool> until, CancellationToken cancellationToken = default) =>
        WaitUntilAsync(token => LogsAsync(traceId, token), until, $"logs for trace '{traceId}'", cancellationToken);

    public Task<IReadOnlyList<SpanEvent>> WaitForSpanEventsAsync(string traceId,
        Func<IReadOnlyList<SpanEvent>, bool> until, CancellationToken cancellationToken = default) =>
        WaitUntilAsync(token => SpanEventsAsync(traceId, token), until, $"span events for trace '{traceId}'", cancellationToken);

    public Task<MetricQueryResult> WaitForMetricAsync(string name, DateTimeOffset start, DateTimeOffset end,
        Func<MetricQueryResult, bool> until, MetricAggregation aggregation = MetricAggregation.Last,
        CancellationToken cancellationToken = default) =>
        WaitUntilAsync(token => QueryMetricAsync(name, start, end, aggregation, token), until,
            $"metric '{name}'", cancellationToken);

    /// <summary>Polls the stored series for a metric until their metadata satisfies <paramref name="until" />.</summary>
    public Task<IReadOnlyList<MetricSeries>> WaitForMetricSeriesAsync(string name,
        Func<IReadOnlyList<MetricSeries>, bool> until, CancellationToken cancellationToken = default) =>
        WaitUntilAsync(token => MetricSeriesAsync(name, token), until, $"series for metric '{name}'", cancellationToken);

    /// <summary>Polls until the trace stores every span in <paramref name="spanIds" /> and returns its stored spans.</summary>
    /// <exception cref="TimeoutException">A span is still not stored at the deadline.</exception>
    public Task<IReadOnlyList<StoredSpan>> WaitForSpansAsync(string traceId, IReadOnlyCollection<string> spanIds,
        CancellationToken cancellationToken = default) =>
        WaitForSpansAsync(traceId, spanIds, static _ => { }, cancellationToken);

    /// <summary>Polls until every expected span is stored or the deadline passes, returning the last stored spans.</summary>
    internal async Task<IReadOnlyList<StoredSpan>> AwaitStoredSpansAsync(string traceId, IReadOnlyCollection<string> spanIds,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<StoredSpan> stored = [];
        try
        {
            return await WaitForSpansAsync(traceId, spanIds, spans => stored = spans, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // The policy reports each captured span the Collector did not store.
        }
        return stored;
    }

    private Task<IReadOnlyList<StoredSpan>> WaitForSpansAsync(string traceId, IReadOnlyCollection<string> spanIds,
        Action<IReadOnlyList<StoredSpan>> observe, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(traceId);
        ArgumentNullException.ThrowIfNull(spanIds);
        return WaitUntilAsync(async token =>
            {
                var spans = (await ReadAsync<StoredTrace>(TraceRoute(traceId), token).ConfigureAwait(false)).Spans;
                observe(spans);
                return spans;
            },
            spans => spanIds.All(id => spans.Any(span => span.SpanId == id)), $"trace '{traceId}'", cancellationToken);
    }

    private async Task<T> WaitUntilAsync<T>(Func<CancellationToken, Task<T>> read, Func<T, bool> until,
        string operation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(until);
        using var timeout = new CancellationTokenSource(WaitDeadline, TimeProvider);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        using var polling = new PeriodicTimer(PollingInterval, TimeProvider);
        try
        {
            do
            {
                try
                {
                    var result = await read(deadline.Token).ConfigureAwait(false);
                    if (until(result)) return result;
                }
                catch (HttpRequestException error) when (error.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                }
            } while (await polling.WaitForNextTickAsync(deadline.Token).ConfigureAwait(false));
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Collector did not return the required evidence for {operation}.", error);
        }

        throw new TimeoutException($"Collector did not return the required evidence for {operation}.");
    }

    internal async Task<HttpResponseMessage> ExportAsync(string route, ReadOnlyMemory<byte> payload, string? contentType,
        string? contentEncoding, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, route);
        request.Content = new ReadOnlyMemoryContent(payload);
        if (contentType is not null)
        {
            request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        }

        if (!string.IsNullOrEmpty(contentEncoding))
        {
            request.Content.Headers.ContentEncoding.Add(contentEncoding);
        }

        return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads every page of logs correlated to one trace, preserving the qyl log records.</summary>
    public async Task<IReadOnlyList<StoredLogRecord>> LogsAsync(string traceId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(traceId);
        var logs = new List<StoredLogRecord>();
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        var route = $"api/v1/logs?trace_id={Uri.EscapeDataString(traceId)}&limit=1000";
        var next = route;
        while (true)
        {
            var page = await ReadAsync<CursorPageLogRecord>(next, cancellationToken).ConfigureAwait(false);
            logs.AddRange(page.Items);
            if (!page.HasMore)
            {
                return logs;
            }

            var cursor = page.NextCursor;
            if (string.IsNullOrEmpty(cursor) || !cursors.Add(cursor))
            {
                throw new InvalidDataException("qyl returned an invalid log continuation cursor.");
            }

            next = $"{route}&cursor={Uri.EscapeDataString(cursor)}";
        }
    }

    /// <summary>Reads every page of stored series for a metric, including resource and instrumentation metadata.</summary>
    public async Task<IReadOnlyList<MetricSeries>> MetricSeriesAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var series = new List<MetricSeries>();
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        var route = $"api/v1/metrics/{Uri.EscapeDataString(name)}/series?limit=1000";
        var next = route;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = await ReadAsync<CursorPageMetricSeries>(next, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            series.AddRange(page.Items);
            if (!page.HasMore)
            {
                return series;
            }

            var cursor = page.NextCursor;
            if (string.IsNullOrEmpty(cursor) || !cursors.Add(cursor))
            {
                throw new InvalidDataException("qyl returned an invalid metric series continuation cursor.");
            }

            next = $"{route}&cursor={Uri.EscapeDataString(cursor)}";
        }
    }

    /// <summary>Reads the span events qyl stored for one trace, across all of its spans.</summary>
    public async Task<IReadOnlyList<SpanEvent>> SpanEventsAsync(string traceId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(traceId);
        var trace = await ReadAsync<StoredTrace>(TraceRoute(traceId), cancellationToken).ConfigureAwait(false);
        return [.. trace.Spans.SelectMany(span => span.Events ?? [])];
    }

    /// <summary>Queries metric buckets for an explicit time window; metrics are not individual trace events.</summary>
    public Task<MetricQueryResult> QueryMetricAsync(string name, DateTimeOffset start, DateTimeOffset end,
        MetricAggregation aggregation = MetricAggregation.Last, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var route = $"api/v1/metrics/{Uri.EscapeDataString(name)}/query"
            + $"?start_time={Timestamp(start)}&end_time={Timestamp(end)}&aggregation={aggregation.ToString().ToLowerInvariant()}";
        return ReadAsync<MetricQueryResult>(route, cancellationToken);
    }

    /// <summary>
    ///     Runs one analysis operation, such as triage or verify_fix, through the Collector's HTTP route. The operation
    ///     is also the MCP tool name; the input is the tool's snake_case wire form.
    /// </summary>
    public async Task<T> AnalyzeAsync<T>(string operation, JsonObject input, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentNullException.ThrowIfNull(input);
        using var content = new StringContent(input.ToJsonString(), System.Text.Encoding.UTF8, "application/json");
        using var response = await client.PostAsync($"api/v1/analysis/{Uri.EscapeDataString(operation)}", content, cancellationToken)
            .ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"{operation} returned {(int)response.StatusCode}: {body}", null, response.StatusCode);
        using var document = JsonDocument.Parse(body);
        ContractJson.Require(ContractTypes.Analysis(operation).Output, document.RootElement);
        return typeof(T) == typeof(JsonElement) ? (T)(object)document.RootElement.Clone()
            : ContractJson.Deserialize<T>(document.RootElement) ?? throw new InvalidDataException($"{operation} returned an empty JSON response.");
    }

    private async Task<T> ReadAsync<T>(string route, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(route, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (body.Trim() == "null") throw new InvalidDataException("qyl returned an empty JSON response.");
        return ContractJson.Deserialize<T>(body) ?? throw new InvalidDataException("qyl returned an empty JSON response.");
    }

    private static string Timestamp(DateTimeOffset value) =>
        Uri.EscapeDataString(value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
}
