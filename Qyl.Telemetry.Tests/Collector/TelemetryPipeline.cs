using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Qyl.Telemetry.SemanticConventions;
using Qyl.Telemetry.SemanticConventions.Names;

namespace Qyl.Telemetry.Tests.Collector;

/// <summary>Owns the canonical OTLP capture, the published Collector and the test process's SDK.</summary>
[JetBrains.Annotations.PublicAPI]
public sealed class TelemetryPipeline(TestContext context, PublishedExecutable? collectorExecutable = null) : IAsyncDisposable
{
    public const string TestProcess = "test";
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentQueue<string> _evidence = new();
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

    public TelemetrySnapshot Capture => _capture?.Snapshot() ?? _lastCapture;
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
                var executable = collectorExecutable ?? PublishedExecutable.Identify(
                    Path.Combine(AppContext.BaseDirectory, "qyl", "collector", "qyl.collector.dll"),
                    ExecutableFormat.ManagedAssembly, System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier);
                _collector = await CollectorFixture.StartAsync(executable, context.TestTempDirectory, cancellationToken).ConfigureAwait(false);
                _capture = await OtlpReceiver.StartAsync(cancellationToken).ConfigureAwait(false);
                _receiver = await OtlpRelay.StartAsync(_capture, _collector.Client, cancellationToken).ConfigureAwait(false);
                _provider = Sdk.CreateTracerProviderBuilder()
                    .SetSampler(new AlwaysOnSampler())
                    .SetResourceBuilder(ResourceBuilder.CreateDefault().AddService(TestProcess,
                        serviceVersion: typeof(TelemetryPipeline).Assembly.GetName().Version!.ToString(3)))
                    .AddSource(QylTelemetryNames.Scopes.QylTelemetryApplication, "System.Net.Http")
                    .AddOtlpExporter(options =>
                    {
                        options.Endpoint = new Uri(_receiver.Address, "/v1/traces");
                        options.Protocol = OtlpExportProtocol.HttpProtobuf;
                        options.Compression = OtlpExportCompression.GZip;
                        options.Headers = "";
                        options.TimeoutMilliseconds = 10_000;
                        options.ExportProcessorType = ExportProcessorType.Simple;
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
        try { await StopAsync().ConfigureAwait(false); }
        catch (Exception error) { errors.Add(error); }
        finally { _gate.Dispose(); }
        foreach (var path in EvidenceFiles)
        {
            try { context.AddResultFile(path); }
            catch (Exception error) { errors.Add(error); }
        }
        if (errors.Count > 0) throw new AggregateException("Telemetry cleanup or evidence attachment failed.", errors);
    }

    private async Task StopCoreAsync()
    {
        var provider = _provider; _provider = null;
        var receiver = _receiver; _receiver = null;
        var capture = _capture; _capture = null;
        var collector = _collector; _collector = null;
        var errors = new List<Exception>();
        try { provider?.Dispose(); }
        catch (Exception error) { errors.Add(error); }
        if (receiver is not null)
        {
            try { await receiver.DisposeAsync().ConfigureAwait(false); }
            catch (Exception error) { errors.Add(error); }
        }
        if (capture is not null)
        {
            try
            {
                _lastCapture = capture.Snapshot();
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
                        .Select(receipt => new InvalidDataException($"Capture rejected {receipt.Rejected} {receipt.Signal} record(s): {receipt.Error}. See {path}.")));
                    var sourcesPath = Path.Combine(collector.DataDirectory, "span-sources.txt");
                    var sources = Sources.OrderBy(source => source.Key, StringComparer.Ordinal)
                        .Select(source => $"{source.Value,5}  {source.Key}");
                    await File.WriteAllLinesAsync(sourcesPath, sources, CancellationToken.None).ConfigureAwait(false);
                    _evidence.Enqueue(sourcesPath);
                }
            }
            catch (Exception error) { errors.Add(error); }
            try { await capture.DisposeAsync().ConfigureAwait(false); }
            catch (Exception error) { errors.Add(error); }
        }
        if (collector is not null)
        {
            try { await collector.DisposeAsync().ConfigureAwait(false); }
            catch (Exception error) { errors.Add(error); }
            foreach (var name in new[] { "shutdown.json", "collector.log" })
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

        var root = Spans.Source.StartActivity(TelemetryActivity.Name, ActivityKind.Internal,
            new ActivityContext(ActivityTraceId.CreateRandom(), default, ActivityTraceFlags.Recorded))
            ?? throw new InvalidOperationException("The span recorder is not listening.");
        _ = root.SetTag("test.name", testName);
        return root;
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
                    : throw new InvalidDataException($"Capture rejected {rejected.Rejected} {rejected.Signal} record(s): {rejected.Error}.");
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

    public void Flush()
    {
        if (_provider?.ForceFlush(10_000) != true)
        {
            throw new InvalidOperationException("The test process could not flush its spans to the receiver.");
        }
    }

    public void End(Activity root)
    {
        root.Stop();
        Flush();
    }
}
