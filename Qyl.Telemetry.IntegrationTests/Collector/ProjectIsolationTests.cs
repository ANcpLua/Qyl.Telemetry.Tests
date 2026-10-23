using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Google.Protobuf;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Logs.V1;
using OpenTelemetry.Proto.Resource.V1;
using OpenTelemetry.Proto.Trace.V1;
using Qyl.Api.Contracts;
using Qyl.Api.Contracts.Common;
using Qyl.Api.Contracts.Common.Errors;
using Qyl.Api.Contracts.Mcp.Tools;
using Qyl.Telemetry.IntegrationTests.Receiver;
using Qyl.Telemetry.Tests.Collector;
using static Qyl.Telemetry.Tests.Evidence.OtlpEvidence;
using ProtoLog = OpenTelemetry.Proto.Logs.V1.LogRecord;
using ProtoSpan = OpenTelemetry.Proto.Trace.V1.Span;
using RpcStatus = Google.Rpc.Status;

namespace Qyl.Telemetry.IntegrationTests.Collector;

[TestClass]
[DoNotParallelize]
public sealed class ProjectIsolationTests(TestContext testContext) : IAsyncDisposable
{
    private const string Alpha = "alpha";
    private const string Beta = "beta";
    private const string AlphaMarker = "alpha-only";
    private const string BetaMarker = "beta-only";
    private const string ServiceName = "project-isolation";
    private const string MetricName = "project.isolation.value";
    private const string ApiKeyHeader = "x-otlp-api-key";
    private const string ProjectHeader = "x-qyl-project";
    private const ulong Millisecond = TimeSpan.TicksPerMillisecond * TimeSpan.NanosecondsPerTick;
    private readonly HttpClient _alphaHttp = new();
    private readonly HttpClient _betaHttp = new();
    private CollectorFixture? _collector;
    private DateTimeOffset _observedAt;

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    [DataRow(Alpha, AlphaMarker)]
    [DataRow(Beta, BetaMarker)]
    public async Task TraceRead_IdenticalIdsInTwoProjects_ReturnsOnlyTheAuthenticatedPayload(string project, string marker)
    {
        await StartAsync();
        await SeedAsync();
        var spans = await Client(project).WaitForSpansAsync(TraceId, [SpanId], testContext.CancellationToken);

        var span = Assert.ContainsSingle(spans);
        Assert.AreEqual(TraceId, span.TraceId);
        Assert.AreEqual(SpanId, span.SpanId);
        Assert.AreEqual(marker, span.Name);
        Assert.AreEqual(marker, span.Status.Message);
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    [DataRow(Alpha, AlphaMarker)]
    [DataRow(Beta, BetaMarker)]
    public async Task LogRead_IdenticalCorrelationIdsInTwoProjects_ReturnsOnlyTheAuthenticatedPayload(string project, string marker)
    {
        await StartAsync();
        await SeedAsync();
        var logs = await Client(project).WaitForLogsAsync(TraceId, records => records.Count > 0, testContext.CancellationToken);

        var log = Assert.ContainsSingle(logs);
        Assert.AreEqual(TraceId, log.TraceId);
        Assert.AreEqual(SpanId, log.SpanId);
        Assert.AreEqual(marker, Assert.IsInstanceOfType<AttributeValue.StringValue>(log.Body).Value);
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    [DataRow(Alpha, 17.25)]
    [DataRow(Beta, 91.5)]
    public async Task MetricRead_IdenticalSeriesAndTimestampInTwoProjects_ReturnsOnlyTheAuthenticatedValue(string project, double expectedMetricValue)
    {
        await StartAsync();
        await SeedAsync();
        var metric = await Client(project).WaitForMetricAsync(MetricName, _observedAt.AddMinutes(-1), _observedAt.AddMinutes(1),
            result => result.Series.Any(series => series.Buckets.Any(bucket => bucket.Value.HasValue)),
            cancellationToken: testContext.CancellationToken);

        Assert.AreEqual(MetricName, metric.Name);
        Assert.IsFalse(metric.Truncated);
        var series = Assert.ContainsSingle(metric.Series);
        Assert.AreSequenceEqual([expectedMetricValue], series.Buckets.Where(bucket => bucket.Value.HasValue).Select(bucket => bucket.Value!.Value));
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    [DataRow(Alpha)]
    [DataRow(Beta)]
    public async Task VerifyFix_ProjectBoundHandle_ReplaysForItsAuthenticatedProject(string project)
    {
        await StartAsync();
        await SeedAsync();
        var client = Client(project);
        _ = await client.WaitForSpansAsync(TraceId, [SpanId], testContext.CancellationToken);
        var issued = await client.AnalyzeAsync<VerifyFixOutput>("verify_fix", VerificationInput(), testContext.CancellationToken);

        var repeated = await client.AnalyzeAsync<VerifyFixOutput>("verify_fix",
            new JsonObject { ["verify_id"] = issued.VerifyId }, testContext.CancellationToken);

        Assert.AreEqual("failed", repeated.Verdict);
        Assert.AreEqual(1L, repeated.After.Requests);
        Assert.AreEqual(1L, repeated.After.Occurrences);
        Assert.AreSequenceEqual([TraceId], repeated.Recurrences);
        Assert.IsNull(repeated.Error);
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    [DataRow(Alpha, Beta)]
    [DataRow(Beta, Alpha)]
    public async Task VerifyFix_OtherProjectsHandle_IsRejectedByTheAuthenticatedBoundary(string issuingProject, string readingProject)
    {
        await StartAsync();
        await SeedAsync();
        var issuer = Client(issuingProject);
        _ = await issuer.WaitForSpansAsync(TraceId, [SpanId], testContext.CancellationToken);
        var issued = await issuer.AnalyzeAsync<VerifyFixOutput>("verify_fix", VerificationInput(), testContext.CancellationToken);
        using var content = new StringContent(new JsonObject { ["verify_id"] = issued.VerifyId }.ToJsonString(), Encoding.UTF8, "application/json");

        using var response = await Http(readingProject).PostAsync("api/v1/analysis/verify_fix", content, testContext.CancellationToken);

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.AreEqual("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var error = ContractJson.Deserialize<ValidationError>(await response.Content.ReadAsStringAsync(testContext.CancellationToken))!;
        Assert.AreEqual(ProblemCode.ValidationFailed, error.Code);
        var detail = Assert.ContainsSingle(error.Errors);
        Assert.AreEqual("verify_id", detail.Field);
        Assert.AreEqual("verify_id.invalid", detail.Code);
        Assert.AreEqual("This handle is invalid or belongs to another project.", detail.Message);
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task TraceRead_ConflictingProjectHeader_IsRejectedBeforeReading()
    {
        await StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"api/v1/traces/{TraceId}");
        request.Headers.Add(ProjectHeader, Beta);

        using var response = await _alphaHttp.SendAsync(request, testContext.CancellationToken);

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        var error = ContractJson.Deserialize<ValidationError>(await response.Content.ReadAsStringAsync(testContext.CancellationToken))!;
        var detail = Assert.ContainsSingle(error.Errors);
        Assert.AreEqual(ProjectHeader, detail.Field);
        Assert.AreEqual("project.mismatch", detail.Code);
        Assert.AreEqual("Project does not match the API key.", detail.Message);
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task TraceIngest_ConflictingResourceProject_IsRejectedBeforePersistence()
    {
        await StartAsync();
        var resource = Resource();
        resource.Attributes.Add(Pair("qyl.project.id", Beta));
        using var content = OtlpHttp.Content(Traces(resource, Span(AlphaMarker)).ToByteArray());

        using var response = await _alphaHttp.PostAsync("v1/traces", content, testContext.CancellationToken);

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        var error = RpcStatus.Parser.ParseFrom(await response.Content.ReadAsByteArrayAsync(testContext.CancellationToken));
        Assert.AreEqual(0, error.Code);
        Assert.AreEqual("The OTLP traces payload is invalid.", error.Message);
        using var alphaRead = await _alphaHttp.GetAsync($"api/v1/traces/{TraceId}", testContext.CancellationToken);
        using var betaRead = await _betaHttp.GetAsync($"api/v1/traces/{TraceId}", testContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.NotFound, alphaRead.StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, betaRead.StatusCode);
    }

    private async Task StartAsync()
    {
        _collector = await CollectorFixture.StartAsync(CollectorFixture.DefaultAssembly, new CollectorFixtureOptions
        {
            ProjectKeys = [new CollectorProjectKey(Alpha, "alpha-test-key"), new CollectorProjectKey(Beta, "beta-test-key")]
        }, testContext.TestTempDirectory, cancellationToken: testContext.CancellationToken);
        _alphaHttp.BaseAddress = _collector.Address;
        _alphaHttp.DefaultRequestHeaders.Add(ApiKeyHeader, "alpha-test-key");
        _betaHttp.BaseAddress = _collector.Address;
        _betaHttp.DefaultRequestHeaders.Add(ApiKeyHeader, "beta-test-key");
        _observedAt = _collector.TimeProvider.GetUtcNow().AddMinutes(-1);
    }

    private async Task SeedAsync()
    {
        await SeedAsync(Alpha, AlphaMarker, 17.25);
        await SeedAsync(Beta, BetaMarker, 91.5);
    }

    private async Task SeedAsync(string project, string marker, double value)
    {
        var client = Client(project);
        using var trace = await client.ExportAsync("v1/traces", Traces(Resource(), Span(marker)).ToByteArray(),
            OtlpReceiver.ProtobufMediaType, null, testContext.CancellationToken);
        _ = trace.EnsureSuccessStatusCode();
        using var log = await client.ExportAsync("v1/logs", Logs(Resource(), new ProtoLog
        {
            TraceId = Id(TraceId), SpanId = Id(SpanId), TimeUnixNano = Timestamp(), ObservedTimeUnixNano = Timestamp(),
            SeverityNumber = SeverityNumber.Info, SeverityText = "INFO", EventName = "isolation.event",
            Body = new AnyValue { StringValue = marker }
        }).ToByteArray(), OtlpReceiver.ProtobufMediaType, null, testContext.CancellationToken);
        _ = log.EnsureSuccessStatusCode();
        var metric = MetricFixture.Of(MetricShapes.Gauge);
        metric.Name = MetricName;
        var point = metric.Gauge.DataPoints[0];
        point.TimeUnixNano = Timestamp();
        point.StartTimeUnixNano = Timestamp();
        point.Flags = 0;
        point.AsDouble = value;
        using var measurement = await client.ExportAsync("v1/metrics", Metrics(Resource(), metric).ToByteArray(),
            OtlpReceiver.ProtobufMediaType, null, testContext.CancellationToken);
        _ = measurement.EnsureSuccessStatusCode();
    }

    private ProtoSpan Span(string marker) => new()
    {
        TraceId = Id(TraceId), SpanId = Id(SpanId), Name = marker, Kind = ProtoSpan.Types.SpanKind.Server,
        StartTimeUnixNano = Timestamp(), EndTimeUnixNano = Timestamp() + Millisecond,
        Status = new Status { Code = Status.Types.StatusCode.Error, Message = marker }
    };

    private static Resource Resource() => new() { Attributes = { Pair("service.name", ServiceName) } };

    private JsonObject VerificationInput() => new()
    {
        ["matchers"] = new JsonArray(new JsonObject { ["key"] = "service.name", ["op"] = "eq", ["value"] = ServiceName }),
        ["since"] = _observedAt.AddSeconds(-1).ToString("O"),
        ["min_requests"] = 0,
        ["min_minutes"] = 0
    };

    private ulong Timestamp() => (ulong)_observedAt.ToUnixTimeMilliseconds() * Millisecond;

    private CollectorClient Client(string project) => new(Http(project), _collector!.TimeProvider);

    private HttpClient Http(string project) => project switch
    {
        Alpha => _alphaHttp,
        Beta => _betaHttp,
        _ => throw new ArgumentOutOfRangeException(nameof(project))
    };

    public async ValueTask DisposeAsync()
    {
        _alphaHttp.Dispose();
        _betaHttp.Dispose();
        if (_collector is null) return;
        try { await _collector.DisposeAsync(); }
        finally
        {
            var log = Path.Combine(_collector.DataDirectory, CollectorFixture.LogFile);
            if (File.Exists(log)) testContext.AddResultFile(log);
        }
    }
}
