using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenTelemetry;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Qyl.Telemetry.SemanticConventions;
using Qyl.Telemetry.SemanticConventions.Names;
using Qyl.Telemetry.Tests.Policies;
using StoredSpan = Qyl.Api.Contracts.OTel.Traces.Span;

namespace Qyl.Telemetry.Tests.Collector;

/// <summary>Owns the canonical OTLP capture, the published Collector and the test process's SDK.</summary>
[JetBrains.Annotations.PublicAPI]
public sealed class TelemetryPipeline(TestContext context, PublishedExecutable? collectorExecutable = null) : IAsyncDisposable
{
    public const string TestProcess = "test";
    private const string AmbientProperty = "qyl.test.ambient";
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentQueue<string> _evidence = new();
    private readonly ConcurrentQueue<(string TraceId, string SpanId)> _roots = new();
    private readonly JsonSerializerOptions _evidenceJson = new()
    {
        WriteIndented = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
    };
    private TelemetrySnapshot _lastCapture = new([], [], [], []);
    private OtlpReceiver? _capture;
    private OtlpRelay? _receiver;
    private CollectorFixture? _collector;
    private TracerProvider? _provider;
    private int _disposed;

    private TelemetrySnapshot Capture => _capture?.Snapshot() ?? _lastCapture;
    public IReadOnlyList<string> EvidenceFiles => [.. _evidence];
    private IReadOnlyDictionary<string, int> Sources => Capture.Spans
        .GroupBy(span => span.Scope?.Name ?? "<unnamed>", StringComparer.Ordinal)
        .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
    public Uri ReceiverAddress => _receiver?.Address
        ?? throw new InvalidOperationException("The trace receiver is not running.");
    public CollectorFixture Collector => _collector
        ?? throw new InvalidOperationException("The qyl collector is not running.");

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_provider is not null)
            {
                return;
            }

            try
            {
                var executable = collectorExecutable ?? PublishedExecutable.Managed(CollectorFixture.DefaultAssembly);
                _collector = await CollectorFixture.StartAsync(executable, context.TestTempDirectory, cancellationToken: cancellationToken).ConfigureAwait(false);
                _capture = await OtlpReceiver.StartAsync(cancellationToken).ConfigureAwait(false);
                _receiver = await OtlpRelay.StartAsync(_capture, _collector.Client, cancellationToken).ConfigureAwait(false);
                _provider = Sdk.CreateTracerProviderBuilder()
                    .SetSampler(new AlwaysOnSampler())
                    .SetResourceBuilder(ResourceBuilder.CreateDefault().AddService(TestProcess,
                        serviceVersion: typeof(TelemetryPipeline).Assembly.GetName().Version!.ToString(3)))
                    .AddSource(QylTelemetryNames.Scopes.QylTelemetryApplication, "System.Net.Http")
                    .AddOtlpCapture(_receiver.Address, configure: options =>
                    {
                        options.Headers = "";
                        options.TimeoutMilliseconds = 10_000;
                    })
                    .Build();
            }
            catch (Exception startup)
            {
                try { await StopCoreAsync().ConfigureAwait(false); }
                catch (Exception cleanup) { throw new AggregateException("Telemetry startup and cleanup both failed.", startup, cleanup); }
                throw;
            }
        }
        finally
        {
            _ = _gate.Release();
        }
    }

    public async Task StopAsync()
    {
        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
        }
        finally { _ = _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        var errors = new List<Exception>();
        await errors.AttemptAsync(() => new ValueTask(StopAsync())).ConfigureAwait(false);
        _gate.Dispose();
        foreach (var path in EvidenceFiles) errors.Attempt(() => context.AddResultFile(path));
        if (errors.Count > 0) throw new AggregateException("Telemetry cleanup or evidence attachment failed.", errors);
    }

    private async Task StopCoreAsync()
    {
        var provider = _provider; _provider = null;
        var receiver = _receiver; _receiver = null;
        var capture = _capture; _capture = null;
        var collector = _collector; _collector = null;
        var errors = new List<Exception>();
        errors.Attempt(() => provider?.Dispose());
        if (receiver is not null) await errors.AttemptAsync(receiver.DisposeAsync).ConfigureAwait(false);
        if (capture is not null)
        {
            await errors.AttemptAsync(async () =>
            {
                _lastCapture = capture.Snapshot();
                var stored = collector is not null && TelemetryInvocation.Requires<PreservedByCollectorAttribute>()
                    ? await ReadBackAsync(collector.Client).ConfigureAwait(false)
                    : null;
                if (collector is not null)
                {
                    var path = Path.Combine(collector.DataDirectory, "capture.json");
                    await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
                    {
                        Capture = _lastCapture,
                        receiver?.Deliveries
                    }, _evidenceJson), CancellationToken.None).ConfigureAwait(false);
                    _evidence.Enqueue(path);
                    var deliveries = receiver?.Deliveries ?? [];
                    if (deliveries.Count != _lastCapture.Requests.Count
                        || deliveries.Any(delivery => delivery.Failure is not null
                            || delivery.CollectorStatus is not (>= 200 and < 300)))
                    {
                        errors.Add(new InvalidDataException($"Not every captured export completed successfully: {_lastCapture.Requests.Count} captured, {deliveries.Count} delivery records. See {path}."));
                    }
                    errors.AddRange(_lastCapture.Receipts.Where(receipt => receipt.Rejected > 0)
                        .Select(receipt => new InvalidDataException($"{Rejection(receipt)} See {path}.")));
                    var sourcesPath = Path.Combine(collector.DataDirectory, "span-sources.txt");
                    var sources = Sources.OrderBy(source => source.Key, StringComparer.Ordinal)
                        .Select(source => $"{source.Value,5}  {source.Key}");
                    await File.WriteAllLinesAsync(sourcesPath, sources, CancellationToken.None).ConfigureAwait(false);
                    _evidence.Enqueue(sourcesPath);
                }
                while (_roots.TryDequeue(out var root))
                {
                    TelemetryInvocation.Publish(new PublishedTrace(new TraceEvidence(root.TraceId, _lastCapture), root.SpanId)
                    {
                        Stored = stored?[root.TraceId]
                    });
                }
            }).ConfigureAwait(false);
            await errors.AttemptAsync(capture.DisposeAsync).ConfigureAwait(false);
        }
        if (collector is not null)
        {
            await errors.AttemptAsync(collector.DisposeAsync).ConfigureAwait(false);
            foreach (var name in new[] { CollectorFixture.ShutdownFile, CollectorFixture.LogFile })
            {
                var path = Path.Combine(collector.DataDirectory, name);
                if (File.Exists(path))
                {
                    _evidence.Enqueue(path);
                }
            }
        }
        if (errors.Count > 0)
        {
            throw new AggregateException("Telemetry shutdown failed; all owned resources were given a cleanup attempt.", errors);
        }
    }

    public Activity Begin(string testName)
    {
        if (_provider is null)
        {
            throw new InvalidOperationException("The span recorder is not listening.");
        }

        // Start() adopts an ambient activity as parent when the context names no parent span, even across traces.
        var ambient = Activity.Current;
        Activity.Current = null;
        var root = Spans.Source.StartActivity(TelemetryActivity.Name, ActivityKind.Internal,
            new ActivityContext(ActivityTraceId.CreateRandom(), default, ActivityTraceFlags.Recorded));
        if (root is null)
        {
            Activity.Current = ambient;
            throw new InvalidOperationException("The span recorder is not listening.");
        }

        _ = root.SetTag("test.name", testName);
        root.SetCustomProperty(AmbientProperty, ambient);
        _roots.Enqueue((root.TraceId.ToHexString(), root.SpanId.ToHexString()));
        return root;
    }

    private async Task<Dictionary<string, IReadOnlyList<StoredSpan>>> ReadBackAsync(CollectorClient client)
    {
        var stored = new Dictionary<string, IReadOnlyList<StoredSpan>>(StringComparer.Ordinal);
        foreach (var traceId in _roots.Select(root => root.TraceId).Distinct(StringComparer.Ordinal))
        {
            var spanIds = _lastCapture.Spans.Where(span => span.TraceId == traceId).Select(span => span.SpanId)
                .ToHashSet(StringComparer.Ordinal);
            stored[traceId] = await client.AwaitStoredSpansAsync(traceId, spanIds, CancellationToken.None).ConfigureAwait(false);
        }
        return stored;
    }

    public IReadOnlyList<SpanRecord> SpansOf(string traceId) =>
        [.. Capture.Spans.Where(span => span.TraceId == traceId)];

    public async Task<TelemetrySnapshot> AwaitAsync(Func<TelemetrySnapshot, bool> until,
        CancellationToken cancellationToken)
    {
        var receiver = _capture ?? throw new InvalidOperationException("The OTLP capture is not running.");
        try
        {
            return await receiver.AwaitAsync(snapshot =>
            {
                var rejected = snapshot.Receipts.FirstOrDefault(receipt => receipt.Rejected > 0);
                return rejected is null ? until(snapshot)
                    : throw new InvalidDataException(Rejection(rejected));
            }, TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException error)
        {
            var capture = receiver.Snapshot();
            throw new TimeoutException($"Required telemetry was not observed. Captured {capture.Spans.Count} spans; "
                + $"log events [{string.Join(", ", capture.Logs.Select(log => string.IsNullOrEmpty(log.EventName) ? log.Body?.ToString() : log.EventName))}]; "
                + $"metrics [{string.Join(", ", capture.Metrics.Select(metric => metric.Name).Distinct())}].", error);
        }
    }

    private static string Rejection(ExportReceipt receipt) =>
        $"Capture rejected {receipt.Rejected} {receipt.Signal} record(s): {receipt.Error}.";

    private void Flush()
    {
        if (_provider?.ForceFlush(10_000) != true)
        {
            throw new InvalidOperationException("The test process could not flush its spans to the receiver.");
        }
    }

    /// <summary>Stops a root from <see cref="Begin" />, restores the caller's ambient activity and flushes.</summary>
    public void End(Activity root)
    {
        ArgumentNullException.ThrowIfNull(root);
        root.Stop();
        Activity.Current = root.GetCustomProperty(AmbientProperty) as Activity;
        Flush();
    }

    /// <summary>Returns a scope that calls <see cref="End" /> for <paramref name="root" /> when it is disposed.</summary>
    public RootScope Ending(Activity root) => new(this, root);

    /// <summary>Ends one root from <see cref="Begin" /> on disposal.</summary>
    public sealed class RootScope(TelemetryPipeline pipeline, Activity root) : IDisposable
    {
        public void Dispose() => pipeline.End(root);
    }
}
