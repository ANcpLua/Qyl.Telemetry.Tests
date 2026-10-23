using Qyl.Orchestration;
using Qyl.Telemetry.SemanticConventions;

namespace Qyl.Telemetry.Tests.Collector;

/// <summary>Owns an instrumented child application and its isolated capture-to-Collector path.</summary>
[JetBrains.Annotations.PublicAPI]
public sealed class InstrumentedApplicationFixture : IAsyncDisposable
{
    public const string ServiceName = "telemetry-probe";
    private readonly QylApplication _application;
    private readonly TestContext _context;
    private readonly string _logPath;
    private int _disposed;

    private InstrumentedApplicationFixture(TelemetryPipeline telemetry, QylApplication application, TestContext context, string logPath)
    {
        Telemetry = telemetry;
        _application = application;
        _context = context;
        _logPath = logPath;
        Client = new HttpClient { BaseAddress = application.GetEndpoint(ServiceName) };
    }

    public TelemetryPipeline Telemetry { get; }
    public HttpClient Client { get; }

    public static Task<InstrumentedApplicationFixture> StartAsync(string assembly, TestContext context,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.CancellationToken.ThrowIfCancellationRequested();
        return StartCoreAsync(PublishedExecutable.Managed(assembly), null, context, environment);
    }

    public static Task<InstrumentedApplicationFixture> StartAsync(PublishedExecutable application,
        PublishedExecutable collector, TestContext context, IReadOnlyDictionary<string, string>? environment = null) =>
        StartCoreAsync(application, collector, context, environment);

    private static async Task<InstrumentedApplicationFixture> StartCoreAsync(PublishedExecutable executable,
        PublishedExecutable? collector, TestContext context, IReadOnlyDictionary<string, string>? environment)
    {
        ArgumentNullException.ThrowIfNull(context);
        var cancellationToken = context.CancellationToken;
        cancellationToken.ThrowIfCancellationRequested();
        executable.VerifyIntegrity();
        var telemetry = new TelemetryPipeline(context, collector);
        QylApplication? application = null;
        string? logPath = null;
        try
        {
            await telemetry.StartAsync(cancellationToken).ConfigureAwait(false);
            logPath = Path.Combine(telemetry.Collector.DataDirectory, "application.log");
            var builder = new QylApplicationBuilder { StartupTimeoutSeconds = 30, MaxRestarts = 0 };
            var service = builder.AddExecutable(ServiceName, executable.Command, executable.Arguments, Path.GetDirectoryName(executable.Path))
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
            return new InstrumentedApplicationFixture(telemetry, application, context, logPath);
        }
        catch (Exception startupFailure)
        {
            var errors = new List<Exception> { startupFailure };
            if (application is not null) await errors.AttemptAsync(application.DisposeAsync).ConfigureAwait(false);
            if (application is not null && logPath is not null)
                await errors.AttemptAsync(() => SaveOutputAsync(application, context, logPath)).ConfigureAwait(false);
            await errors.AttemptAsync(telemetry.DisposeAsync).ConfigureAwait(false);
            if (errors.Count > 1) throw new AggregateException("Application startup and cleanup failed.", errors);
            throw;
        }
    }

    /// <summary>
    ///     Sends <paramref name="request" /> to the application under a new pipeline root, lets <paramref name="inspect" />
    ///     read the response before the root ends, and waits until the application's server span of that trace is captured.
    /// </summary>
    public async Task<ApplicationRoundTrip> RoundTripAsync(HttpRequestMessage request, Func<HttpResponseMessage, Task> inspect,
        TestContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(inspect);
        ArgumentNullException.ThrowIfNull(context);
        using var root = Telemetry.Begin(context.TestName);
        var traceId = root.TraceId.ToHexString();
        using (Telemetry.Ending(root))
        {
            using var response = await Client.SendAsync(request, context.CancellationToken).ConfigureAwait(false);
            await inspect(response).ConfigureAwait(false);
        }

        var capture = await Telemetry.AwaitAsync(snapshot => snapshot.Spans.Any(span =>
            span.TraceId == traceId && span is { Service: ServiceName, Kind: SpanKind.Server }), context.CancellationToken).ConfigureAwait(false);
        return new ApplicationRoundTrip(root.Context, new TraceEvidence(traceId, capture));
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Client.Dispose();
        var errors = new List<Exception>();
        await errors.AttemptAsync(_application.DisposeAsync).ConfigureAwait(false);
        await errors.AttemptAsync(() => SaveOutputAsync(_application, _context, _logPath)).ConfigureAwait(false);
        await errors.AttemptAsync(Telemetry.DisposeAsync).ConfigureAwait(false);
        if (errors.Count > 0) throw new AggregateException("Application fixture cleanup failed.", errors);
    }

    private static async ValueTask SaveOutputAsync(QylApplication application, TestContext context, string path)
    {
        await File.WriteAllLinesAsync(path, application.GetOutput(ServiceName), CancellationToken.None).ConfigureAwait(false);
        context.AddResultFile(path);
    }
}
