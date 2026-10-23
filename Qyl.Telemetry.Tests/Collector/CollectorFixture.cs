using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace Qyl.Telemetry.Tests.Collector;

/// <summary>Owns an isolated instance of the local qyl Collector for a test run.</summary>
[JetBrains.Annotations.PublicAPI]
public sealed class CollectorFixture : IAsyncDisposable
{
    /// <summary>The exit code and forced-kill flag written to the data directory on disposal.</summary>
    internal const string ShutdownFile = "shutdown.json";

    /// <summary>The Collector's standard output and error, written to the data directory on disposal.</summary>
    internal const string LogFile = "collector.log";

    private readonly OwnedProcess _process;
    private readonly HttpClient _client;
    private readonly ChildProcess.OutputCapture _output;
    private readonly ChildProcess.OutputCapture _error;
    private readonly CancellationTokenSource _outputCancellation;
    private bool _disposed;
    private bool _ready;

    private readonly PublishedExecutable _executable;
    private readonly string? _projectKeysJson;

    private CollectorFixture(OwnedProcess process, PublishedExecutable executable, Uri address, string directory,
        TimeProvider timeProvider, string? projectKeysJson)
    {
        _process = process;
        _executable = executable;
        _projectKeysJson = projectKeysJson;
        Address = address;
        DataDirectory = directory;
        TimeProvider = timeProvider;
        _client = new HttpClient { BaseAddress = address };
        _outputCancellation = new CancellationTokenSource(Timeout.InfiniteTimeSpan, timeProvider);
        _output = new ChildProcess.OutputCapture(process.StandardOutput, null, _outputCancellation.Token);
        _error = new ChildProcess.OutputCapture(process.StandardError, null, _outputCancellation.Token);
        Client = new CollectorClient(_client, timeProvider);
    }

    /// <summary>The local Collector that a project with UsesQylCollector builds and copies next to its test assembly.</summary>
    public static string DefaultAssembly { get; } = Path.Combine(AppContext.BaseDirectory, "qyl", "collector", "qyl.collector.dll");

    public Uri Address { get; }
    public CollectorClient Client { get; }
    public string DataDirectory { get; }

    /// <summary>The clock this Collector's startup, shutdown and evidence waits run on.</summary>
    public TimeProvider TimeProvider { get; }

    internal int ProcessId => _process.Id;
    internal Task WaitForExitAsync(CancellationToken cancellationToken) => _process.WaitForExitAsync(cancellationToken);

    public static Task<CollectorFixture> StartAsync(string assembly, string? workspace = null,
        TimeProvider? timeProvider = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return StartAsync(PublishedExecutable.Managed(assembly), workspace, timeProvider, cancellationToken);
    }

    public static Task<CollectorFixture> StartAsync(PublishedExecutable executable, string? workspace = null,
        TimeProvider? timeProvider = null, CancellationToken cancellationToken = default) =>
        StartAsync(executable, new CollectorFixtureOptions(), workspace, timeProvider, cancellationToken);

    /// <summary>Starts a Collector with explicit project credentials and no inherited qyl settings.</summary>
    public static Task<CollectorFixture> StartAsync(string assembly, CollectorFixtureOptions options,
        string? workspace = null, TimeProvider? timeProvider = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return StartAsync(PublishedExecutable.Managed(assembly), options, workspace, timeProvider, cancellationToken);
    }

    /// <summary>Starts an identified Collector with explicit project credentials.</summary>
    public static Task<CollectorFixture> StartAsync(PublishedExecutable executable, CollectorFixtureOptions options,
        string? workspace = null, TimeProvider? timeProvider = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(options);
        var projectKeysJson = options.ProjectKeysJson();
        var directory = Path.Combine(workspace ?? Path.GetTempPath(), "qyl-collector-" + Guid.NewGuid().ToString("N"));
        return StartAsync(executable, directory, created: true, timeProvider ?? TimeProvider.System, projectKeysJson, cancellationToken);
    }

    /// <summary>Stops this Collector gracefully and starts the same executable on the same data directory.</summary>
    public async Task<CollectorFixture> RestartAsync(CancellationToken cancellationToken = default)
    {
        await DisposeAsync().ConfigureAwait(false);
        return await StartAsync(_executable, DataDirectory, created: false, TimeProvider, _projectKeysJson, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<CollectorFixture> StartAsync(PublishedExecutable executable, string directory, bool created,
        TimeProvider timeProvider, string? projectKeysJson, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var start = executable.StartInfo();
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        if (created) _ = Directory.CreateDirectory(directory);
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        foreach (var key in start.Environment.Keys.Where(key => key.StartsWith("QYL_", StringComparison.Ordinal)
                     || key.StartsWith("OTEL_", StringComparison.Ordinal)).ToArray())
        {
            _ = start.Environment.Remove(key);
        }

        start.Environment["QYL_PORT"] = port.ToString(CultureInfo.InvariantCulture);
        start.Environment["QYL_OTLP_PORT"] = "0";
        start.Environment["QYL_GRPC_PORT"] = "0";
        start.Environment["QYL_BIND_ADDRESS"] = "127.0.0.1";
        start.Environment["QYL_OTLP_AUTH_MODE"] = projectKeysJson is null ? "Unsecured" : "ApiKey";
        if (projectKeysJson is not null) start.Environment["QYL_OTLP_PROJECT_KEYS"] = projectKeysJson;
        start.Environment["QYL_DATA_PATH"] = Path.Combine(directory, "telemetry.duckdb");
        start.Environment["QYL_DB_THREADS"] = "2";
        start.Environment["OTEL_TRACES_EXPORTER"] = "none";
        start.Environment["OTEL_METRICS_EXPORTER"] = "none";
        start.Environment["OTEL_LOGS_EXPORTER"] = "none";
        start.Environment["Logging__LogLevel__Default"] = "Warning";
        reservation.Stop();
        var process = await OwnedProcess.StartAsync(start, cancellationToken, timeProvider).ConfigureAwait(false);
        var collector = new CollectorFixture(process, executable, new Uri($"http://127.0.0.1:{port}/"), directory, timeProvider, projectKeysJson);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30), timeProvider);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        var startup = WaitUntilReadyAsync(deadline.Token);
        try
        {
            return await startup.ConfigureAwait(false);
        }
        catch (Exception startupFailure)
        {
            try { await collector.DisposeAsync().ConfigureAwait(false); }
            catch (Exception cleanupFailure)
            {
                throw new AggregateException("Collector startup and cleanup both failed.", startupFailure, cleanupFailure);
            }
            return await startup.ConfigureAwait(false);
        }

        async Task<CollectorFixture> WaitUntilReadyAsync(CancellationToken token)
        {
            while (true)
            {
                if (process.HasExited)
                {
                    var code = process.ExitCode;
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10), timeProvider);
                    await process.KillAsync(cleanup.Token).ConfigureAwait(false);
                    await Task.WhenAll(collector._output.Completion, collector._error.Completion).WaitAsync(cleanup.Token).ConfigureAwait(false);
                    throw new InvalidOperationException($"qyl exited with code {code}: {collector._output.Text}{collector._error.Text}");
                }

                try
                {
                    using var response = await collector._client.GetAsync("health", token).ConfigureAwait(false);
                    _ = response.EnsureSuccessStatusCode();
                    collector._ready = true;
                    return collector;
                }
                catch (HttpRequestException)
                {
                    await Task.Delay(CollectorClient.PollingInterval, timeProvider, token).ConfigureAwait(false);
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        var forced = false;
        var signalled = false;
        int? exitCode = null;
        TimeoutException? outputFailure = null;
        var log = Path.Combine(DataDirectory, LogFile);
        try
        {
            if (!_process.HasExited)
            {
                signalled = true;
                if (!OperatingSystem.IsWindows())
                {
                    var signal = new ProcessStartInfo("/bin/kill")
                    {
                        ArgumentList = { "-TERM", _process.Id.ToString(CultureInfo.InvariantCulture) },
                        UseShellExecute = false
                    };
                    using var signalDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(10), TimeProvider);
                    var result = await ChildProcess.RunAsync(signal, "the Collector shutdown signal", signalDeadline.Token,
                        new ChildProcessOptions { TimeProvider = TimeProvider }).ConfigureAwait(false);
                    if (result.ExitCode != 0 && !_process.HasExited)
                        throw new InvalidOperationException($"Could not signal the Collector: {result.Error}");
                }
                else
                {
                    _ = _process.CloseMainWindow();
                }
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10), TimeProvider);
                try { await _process.WaitForExitAsync(deadline.Token).ConfigureAwait(false); }
                catch (OperationCanceledException)
                {
                    forced = true;
                }
            }
        }
        finally
        {
            try
            {
                if (!_process.HasExited) forced = true;
                using var forceDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(10), TimeProvider);
                await _process.KillAsync(forceDeadline.Token).ConfigureAwait(false);
                exitCode = _process.ExitCode;
                _outputCancellation.CancelAfter(TimeSpan.FromSeconds(10));
                await Task.WhenAll(_output.Completion, _error.Completion).WaitAsync(_outputCancellation.Token).ConfigureAwait(false);
                if (!_output.Ended || !_error.Ended)
                    outputFailure = new TimeoutException($"The Collector output did not close within 10 seconds. See {log}.");
            }
            finally
            {
                try
                {
                    await File.WriteAllTextAsync(Path.Combine(DataDirectory, ShutdownFile),
                        JsonSerializer.Serialize(new { ExitCode = exitCode, Forced = forced })).ConfigureAwait(false);
                    await File.WriteAllTextAsync(log, _output.Text + _error.Text).ConfigureAwait(false);
                }
                finally
                {
                    await _outputCancellation.CancelAsync().ConfigureAwait(false);
                    _outputCancellation.Dispose();
                    _client.Dispose();
                    await _process.DisposeAsync().ConfigureAwait(false);
                }
            }
        }

        if (outputFailure is not null) throw outputFailure;
        switch (_ready, signalled, OperatingSystem.IsWindows(), forced, exitCode)
        {
            case (true, false, _, _, _):
                throw new InvalidOperationException($"The Collector exited unexpectedly after becoming ready with code {exitCode}. See {log}.");
            case (true, true, false, true, _):
                throw new InvalidOperationException($"The Collector ignored SIGTERM for 10 seconds and was killed. See {log}.");
            case (true, true, false, _, not 0):
                throw new InvalidOperationException($"The Collector exited with code {exitCode} after SIGTERM. See {log}.");
        }
    }
}
