using Qyl;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Qyl.Telemetry.EndToEndTests;

var builder = WebApplication.CreateSlimBuilder(args);
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, ProbeJsonContext.Default));
builder.Logging.AddFilter(ProbeSignals.MeterName, LogLevel.Information);
builder.AddQyl(options => options.AdditionalMeters.Add(ProbeSignals.MeterName));
using var meter = new Meter(ProbeSignals.MeterName);
var requests = meter.CreateCounter<long>(ProbeSignals.CounterName, ProbeSignals.CounterUnit);
var app = builder.Build();
var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(ProbeSignals.MeterName);
var completed = LoggerMessage.Define<string>(LogLevel.Information, new EventId(4100, ProbeSignals.CompletedLog),
    "Probe request {ProbeRequestId} completed");
app.MapGet("/health", () => TypedResults.Ok(Environment.ProcessId));
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
    return TypedResults.Ok(new ProbeReply(true, logger.IsEnabled(LogLevel.Information), RuntimeFeature.IsDynamicCodeSupported));
});
app.MapGet("/failure", Failure);
app.Run();
return;

static IResult Failure() => throw new InvalidOperationException(ProbeSignals.FailureMessage);

internal sealed record ProbeReply(bool Captured, bool InformationLoggingEnabled, bool IsDynamicCodeSupported);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ProbeReply))]
[JsonSerializable(typeof(int))]
internal partial class ProbeJsonContext : JsonSerializerContext;
