using Qyl.Orchestration;

namespace Qyl.Telemetry.Tests.Collector;

/// <summary>Owns an instrumented child application and its isolated capture-to-Collector path.</summary>
[JetBrains.Annotations.PublicAPI]
public sealed class InstrumentedApplicationFixture : IAsyncDisposable
{
    private const string ServiceName = "telemetry-probe";
    private readonly QylApplication _application;
    private int _disposed;

    private InstrumentedApplicationFixture(TelemetryPipeline telemetry, QylApplication application)
    {
        Telemetry = telemetry;
        _application = application;
        Client = new HttpClient { BaseAddress = application.GetEndpoint(ServiceName) };
    }

    public TelemetryPipeline Telemetry { get; }
    public HttpClient Client { get; }

    public static async Task<InstrumentedApplicationFixture> StartAsync(string assembly, TestContext context,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        var cancellationToken = context.CancellationToken;
        var telemetry = new TelemetryPipeline(context);
        QylApplication? application = null;
        try
        {
            await telemetry.StartAsync(cancellationToken).ConfigureAwait(false);
            var builder = new QylApplicationBuilder { StartupTimeoutSeconds = 30, MaxRestarts = 0 };
            var service = builder.AddDotnetService(ServiceName, assembly)
                .WithoutEnvironmentPrefix("OTEL_")
                .WithoutEnvironmentPrefix("QYL_")
                .WithoutEnvironmentPrefix("services__")
                .WithoutEnvironmentPrefix("Logging__")
                .WithEnvironment("DOTNET_ENVIRONMENT", "Production")
                .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Production")
                .WithEnvironment("Logging__LogLevel__Default", "Information")
                .WithEnvironment("OTEL_EXPORTER_OTLP_ENDPOINT", telemetry.ReceiverAddress.ToString())
                .WithEnvironment("OTEL_EXPORTER_OTLP_PROTOCOL", "http/protobuf")
                .WithEnvironment("OTEL_TRACES_SAMPLER", "always_on")
                .WithEnvironment("OTEL_BSP_SCHEDULE_DELAY", "100")
                .WithEnvironment("OTEL_BLRP_SCHEDULE_DELAY", "100")
                .WithEnvironment("OTEL_METRIC_EXPORT_INTERVAL", "100")
                .WithHealthCheck(requireProcessId: true);
            if (environment is not null)
            {
                foreach (var (name, value) in environment) _ = service.WithEnvironment(name, value);
            }

            application = builder.Build();
            await application.StartAsync(cancellationToken).ConfigureAwait(false);
            return new InstrumentedApplicationFixture(telemetry, application);
        }
        catch (Exception startupFailure)
        {
            var errors = new List<Exception> { startupFailure };
            if (application is not null)
            {
                try { await application.DisposeAsync().ConfigureAwait(false); }
                catch (Exception cleanupFailure) { errors.Add(cleanupFailure); }
            }

            try { await telemetry.DisposeAsync().ConfigureAwait(false); }
            catch (Exception cleanupFailure) { errors.Add(cleanupFailure); }
            if (errors.Count > 1) throw new AggregateException("Application startup and cleanup failed.", errors);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Client.Dispose();
        var errors = new List<Exception>();
        try { await _application.DisposeAsync().ConfigureAwait(false); }
        catch (Exception error) { errors.Add(error); }
        try { await Telemetry.DisposeAsync().ConfigureAwait(false); }
        catch (Exception error) { errors.Add(error); }
        if (errors.Count > 0) throw new AggregateException("Application fixture cleanup failed.", errors);
    }
}
