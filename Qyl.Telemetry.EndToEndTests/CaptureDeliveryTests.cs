using System.Net;
using System.Text.Json;
using Qyl.Telemetry.SemanticConventions.Attributes.Http;
using Qyl.Telemetry.Tests.Collector;
using SpanKind = Qyl.Telemetry.SemanticConventions.SpanKind;

namespace Qyl.Telemetry.EndToEndTests;

[ConnectedTrace]
[NoDuplicateSpans]
[ConformsToSemanticConventions]
[PreservedByCollector]
public abstract class CaptureDeliveryFixture(TestContext context) : IAsyncDisposable
{
    protected const string ProbeService = "telemetry-probe";
    protected const string RequestIdHeader = "X-Request-Id";
    protected const string UnderscoreHeader = "X_Request_Id";
    protected const string AuthorizationHeader = "Authorization";
    protected const string RequestIdAttribute = HttpAttributes.RequestHeader + ".x-request-id";
    protected const string UnderscoreAttribute = HttpAttributes.RequestHeader + ".x_request_id";
    protected const string AuthorizationAttribute = HttpAttributes.RequestHeader + ".authorization";
    protected const string ResponseIdAttribute = HttpAttributes.ResponseHeader + ".x-response-id";
    private static readonly string[] RequestValues = ["request-1", "request-2"];
    // HttpClient transmits one comma-joined field; capture must not split that exposed value.
    protected static readonly string[] CapturedRequestValues = ["request-1, request-2"];
    protected static readonly string[] UnderscoreValues = ["underscore-1"];
    protected static readonly string[] AuthorizationValues = ["fixture-only-credential"];
    protected static readonly string[] ResponseValues = ["response-1", "response-2"];

    protected virtual IReadOnlyDictionary<string, string>? CaptureEnvironment => null;
    private InstrumentedApplicationFixture? _application;
    protected InstrumentedApplicationFixture Application => _application
        ?? throw new InvalidOperationException("The instrumented application has not started.");
    protected TestContext Context => context;

    [TestInitialize]
    public async Task StartApplicationAsync()
    {
        var assembly = Path.Combine(AppContext.BaseDirectory, "fixtures", "TelemetryProbe", "TelemetryProbe.dll");
        _application = await InstrumentedApplicationFixture.StartAsync(assembly, context,
            CaptureEnvironment);
    }

    protected async Task<TraceEvidence> SendRequestAsync(string route = "capture", HttpStatusCode expectedStatus = HttpStatusCode.OK)
    {
        using var root = Application.Telemetry.Begin(context.TestName);
        var traceId = root.TraceId.ToHexString();
        using (Application.Telemetry.Ending(root))
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, route);
            Assert.IsTrue(request.Headers.TryAddWithoutValidation(ProbeSignals.RequestHeader, ProbeSignals.RequestId));
            Assert.IsTrue(request.Headers.TryAddWithoutValidation(RequestIdHeader, RequestValues));
            Assert.IsTrue(request.Headers.TryAddWithoutValidation(UnderscoreHeader, UnderscoreValues));
            Assert.IsTrue(request.Headers.TryAddWithoutValidation(AuthorizationHeader, AuthorizationValues));
            using var response = await Application.Client.SendAsync(request, context.CancellationToken);
            Assert.AreEqual(expectedStatus, response.StatusCode);
            if (route == "capture")
            {
                using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(context.CancellationToken));
                Assert.IsTrue(body.RootElement.GetProperty("informationLoggingEnabled").GetBoolean());
            }
        }

        var capture = await Application.Telemetry.AwaitAsync(
            snapshot => snapshot.Spans.Any(span => span.TraceId == traceId && span is { Service: ProbeService, Kind: SpanKind.Server }), context.CancellationToken);
        var evidence = new TraceEvidence(traceId, capture);
        var server = Assert.HasSingleNativeSpan(evidence.Spans.Where(span => span.Service == ProbeService),
            "Microsoft.AspNetCore", "GET /" + route, SpanKind.Server);
        var client = Assert.ContainsSingle(evidence.Spans.Where(span => span.Kind == SpanKind.Client
            && span.SpanId == server.ParentSpanId));
        Assert.AreEqual(TelemetryPipeline.TestProcess, client.Service);
        Assert.HasParentContext(server, client);
        Assert.HasParentContext(client, root.Context);
        return evidence;
    }

    public async ValueTask DisposeAsync()
    {
        if (_application is not null) await _application.DisposeAsync();
        GC.SuppressFinalize(this);
    }
}

[TracePerTest]
[DoNotParallelize]
[NoErrorTelemetry]
[ExportsCleanly]
public sealed class SelectedHeaderDeliveryTests(TestContext context) : CaptureDeliveryFixture(context)
{
    private const string CaptureRequestSetting = "OTEL_DOTNET_AUTO_TRACES_ASPNETCORE_INSTRUMENTATION_CAPTURE_REQUEST_HEADERS";
    private const string CaptureResponseSetting = "OTEL_DOTNET_AUTO_TRACES_ASPNETCORE_INSTRUMENTATION_CAPTURE_RESPONSE_HEADERS";
    protected override IReadOnlyDictionary<string, string> CaptureEnvironment => new Dictionary<string, string>
    {
        [CaptureRequestSetting] = $"{RequestIdHeader},{UnderscoreHeader},{AuthorizationHeader}",
        [CaptureResponseSetting] = "X-Response-Id"
    };

    [TestMethod]
    [CapturesExactly(RequestIdAttribute, UnderscoreAttribute, AuthorizationAttribute, ResponseIdAttribute)]
    public async Task Selected_arrays_keep_names_values_and_order_through_capture_and_storage()
    {
        var evidence = await SendRequestAsync();
        var server = Assert.ContainsSingle(evidence.Spans.Where(span => span.Service == ProbeService));
        Assert.AreEqual(SpanKind.Server, server.Kind);
        Assert.AreSequenceEqual(CapturedRequestValues, server.GetStrings(RequestIdAttribute)!);
        Assert.AreSequenceEqual(UnderscoreValues, server.GetStrings(UnderscoreAttribute)!);
        Assert.AreSequenceEqual(AuthorizationValues, server.GetStrings(AuthorizationAttribute)!);
        Assert.AreSequenceEqual(ResponseValues, server.GetStrings(ResponseIdAttribute)!);
    }
}

[TracePerTest]
[DoNotParallelize]
[NoErrorTelemetry]
[ExportsCleanly]
public sealed class UnconfiguredHeaderDeliveryTests(TestContext context) : CaptureDeliveryFixture(context)
{
    [TestMethod]
    [CapturesExactly]
    public async Task Unconfigured_capture_emits_no_request_or_response_header_attributes() => _ = await SendRequestAsync();
}
