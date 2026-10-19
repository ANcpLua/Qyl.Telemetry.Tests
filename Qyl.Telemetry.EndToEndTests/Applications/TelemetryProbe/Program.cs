using Qyl;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Qyl.Telemetry.EndToEndTests;

var builder = WebApplication.CreateBuilder(args);
// This workload proves its own log producer; framework operational logs are a separate contract.
builder.Logging.AddFilter(static (category, level) =>
    category == ProbeSignals.MeterName && level >= LogLevel.Information);
builder.AddQyl(options => options.AdditionalMeters.Add(ProbeSignals.MeterName));
using var meter = new Meter(ProbeSignals.MeterName);
var requests = meter.CreateCounter<long>(ProbeSignals.CounterName, ProbeSignals.CounterUnit);
var app = builder.Build();
var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(ProbeSignals.MeterName);
var completed = LoggerMessage.Define<string>(LogLevel.Information, new EventId(4100, ProbeSignals.CompletedLog),
    "Probe request {ProbeRequestId} completed");
app.MapGet("/health", () => Environment.ProcessId);
app.MapGet("/capture", (HttpContext context) =>
{
    context.Response.Headers.Append("X-Response-Id", "response-1");
    context.Response.Headers.Append("X-Response-Id", "response-2");
    var requestId = context.Request.Headers[ProbeSignals.RequestHeader].ToString();
    completed(logger, requestId, null);
    requests.Add(1);
    Activity.Current?.AddEvent(new ActivityEvent(ProbeSignals.CompletedEvent, tags: new ActivityTagsCollection
    {
        [ProbeSignals.RequestAttribute] = requestId
    }));
    return Results.Ok(new { Captured = true, InformationLoggingEnabled = logger.IsEnabled(LogLevel.Information) });
});
app.MapGet("/failure", Failure);
app.Run();
return;

static IResult Failure() => throw new InvalidOperationException(ProbeSignals.FailureMessage);
