using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace Qyl.Telemetry.Tests.Collector;

/// <summary>Owns an isolated instance of the published qyl collector for a test run.</summary>
[JetBrains.Annotations.PublicAPI]
public sealed class CollectorFixture : IAsyncDisposable
{
    /// <summary>The exit code and forced-kill flag written to the data directory on disposal.</summary>
    internal const string ShutdownFile = "shutdown.json";

    /// <summary>The Collector's standard output and error, written to the data directory on disposal.</summary>
    internal const string LogFile = "collector.log";

    private readonly Process _process;
    private readonly HttpClient _client;
    private readonly Task<string> _output;
    private readonly Task<string> _error;
    private bool _disposed;
    private bool _ready;

    private readonly PublishedExecutable _executable;

    private CollectorFixture(Process process, PublishedExecutable executable, Uri address, string directory,
        TimeProvider timeProvider)
    {
        _process = process;
        _executable = executable;
        Address = address;
        DataDirectory = directory;
        TimeProvider = timeProvider;
        _client = new HttpClient { BaseAddress = address };
        _output = process.StandardOutput.ReadToEndAsync();
        _error = process.StandardError.ReadToEndAsync();
        Client = new CollectorClient(_client, timeProvider);
    }

    /// <summary>The packaged managed Collector that a project with UsesQylCollector copies next to its test assembly.</summary>
    public static string DefaultAssembly { get; } = Path.Combine(AppContext.BaseDirectory, "qyl", "collector", "qyl.collector.dll");

    public Uri Address { get; }
    public CollectorClient Client { get; }
    public string DataDirectory { get; }

    /// <summary>The clock this Collector's startup, shutdown and evidence waits run on.</summary>
    public TimeProvider TimeProvider { get; }

    public static Task<CollectorFixture> StartAsync(string assembly, string? workspace = null,
        TimeProvider? timeProvider = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return StartAsync(PublishedExecutable.Managed(assembly), workspace, timeProvider, cancellationToken);
    }

    public static Task<CollectorFixture> StartAsync(PublishedExecutable executable, string? workspace = null,
        TimeProvider? timeProvider = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var directory = Path.Combine(workspace ?? Path.GetTempPath(), "qyl-collector-" + Guid.NewGuid().ToString("N"));
        return StartAsync(executable, directory, created: true, timeProvider ?? TimeProvider.System, cancellationToken);
    }

    /// <summary>Stops this Collector gracefully and starts the same executable on the same data directory.</summary>
    public async Task<CollectorFixture> RestartAsync(CancellationToken cancellationToken = default)
    {
        await DisposeAsync().ConfigureAwait(false);
        return await StartAsync(_executable, DataDirectory, created: false, TimeProvider, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<CollectorFixture> StartAsync(PublishedExecutable executable, string directory, bool created,
        TimeProvider timeProvider, CancellationToken cancellationToken)
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
        start.Environment["QYL_OTLP_AUTH_MODE"] = "Unsecured";
        start.Environment["QYL_DATA_PATH"] = Path.Combine(directory, "telemetry.duckdb");
        start.Environment["QYL_DB_THREADS"] = "2";
        start.Environment["OTEL_TRACES_EXPORTER"] = "none";
        start.Environment["OTEL_METRICS_EXPORTER"] = "none";
        start.Environment["OTEL_LOGS_EXPORTER"] = "none";
        start.Environment["Logging__LogLevel__Default"] = "Warning";
        reservation.Stop();
        var process = Process.Start(start)!;
        var collector = new CollectorFixture(process, executable, new Uri($"http://127.0.0.1:{port}/"), directory, timeProvider);
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
                    throw new InvalidOperationException($"qyl exited with code {process.ExitCode}: {await collector._output.ConfigureAwait(false)}{await collector._error.ConfigureAwait(false)}");
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
        try
        {
            if (!_process.HasExited)
            {
                signalled = true;
                if (!OperatingSystem.IsWindows())
                {
                    using var signal = Process.Start(new ProcessStartInfo("/bin/kill")
                    {
                        ArgumentList = { "-TERM", _process.Id.ToString(CultureInfo.InvariantCulture) },
                        UseShellExecute = false
                    }) ?? throw new InvalidOperationException("Could not signal the Collector.");
                    await signal.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
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
                    _process.Kill(entireProcessTree: true);
                    await _process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }
            await File.WriteAllTextAsync(Path.Combine(DataDirectory, ShutdownFile),
                JsonSerializer.Serialize(new { _process.ExitCode, Forced = forced })).ConfigureAwait(false);
            var log = Path.Combine(DataDirectory, LogFile);
            await File.WriteAllTextAsync(log,
                await _output.ConfigureAwait(false) + await _error.ConfigureAwait(false)).ConfigureAwait(false);
            // A Collector that became ready must finish its final writes and exit cleanly once signalled.
            if (_ready && signalled && !OperatingSystem.IsWindows() && (forced || _process.ExitCode != 0))
                throw new InvalidOperationException(forced
                    ? $"The Collector ignored SIGTERM for 10 seconds and was killed. See {log}."
                    : $"The Collector exited with code {_process.ExitCode} after SIGTERM. See {log}.");
        }
        finally
        {
            _client.Dispose();
            _process.Dispose();
        }
    }
}
