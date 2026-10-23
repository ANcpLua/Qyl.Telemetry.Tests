using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Qyl.Telemetry.SemanticConventions.Attributes.Http;
using Qyl.Telemetry.Tests.Collector;

namespace Qyl.Telemetry.EndToEndTests.Delivery;

[TracePerTest]
public sealed class DeliveryTests(TestContext context)
{
    private const string TraceTool = "get_trace";
    private const string CapturedKey = HttpAttributes.RequestHeader + ".x-probe-id";
    private static PreparedDelivery? s_delivery;

    [ClassInitialize]
    [Timeout(1_800_000, CooperativeCancellation = true)]
    public static async Task PublishArtifactsAsync(TestContext context) =>
        s_delivery = await new DeliveryArtifacts(context).PrepareAsync();

    [TestMethod]
    [ConnectedTrace]
    [NoDuplicateSpans]
    [PreservedByCollector]
    [DataRow(ExecutableFormat.ManagedAssembly)]
    [DataRow(ExecutableFormat.NativeAot)]
    [Timeout(180_000, CooperativeCancellation = true)]
    public async Task PublishedPackages_ManagedOrNativeAotApplication_ReachTheNativeCollectorAndRealMcp(ExecutableFormat format)
    {
        Assert.IsNotNull(s_delivery);
        context.AddResultFile(s_delivery.ManifestPath);
        var artifact = Application(s_delivery, format);
        await using var application = await InstrumentedApplicationFixture.StartAsync(artifact, s_delivery.NativeCollector,
            context, new Dictionary<string, string>
            {
                ["OTEL_DOTNET_AUTO_TRACES_ASPNETCORE_INSTRUMENTATION_CAPTURE_REQUEST_HEADERS"] = ProbeSignals.RequestHeader
            });
        using var request = new HttpRequestMessage(HttpMethod.Get, "capture");
        request.Headers.Add(ProbeSignals.RequestHeader, ProbeSignals.RequestId);
        var roundTrip = await application.RoundTripAsync(request, async response =>
        {
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(context.CancellationToken));
            Assert.AreEqual(format == ExecutableFormat.ManagedAssembly, body.RootElement.GetProperty("isDynamicCodeSupported").GetBoolean());
        }, context);
        var traceId = roundTrip.Evidence.TraceId;
        var server = Assert.That.HasServerSpanUnderTheTestClient(roundTrip, "GET /capture");
        Assert.AreSequenceEqual([ProbeSignals.RequestId], server.GetStrings(CapturedKey)!);
        _ = await application.Telemetry.Collector.Client.WaitForSpansAsync(traceId, [server.SpanId], context.CancellationToken);

        s_delivery.Mcp.Verify();
        await using var mcp = await McpProcess.ConnectAsync("node", s_delivery.Mcp.EntryPoint, application.Telemetry.Collector.Address,
            line => context.WriteLine("MCP: " + line), context.CancellationToken);
        var tools = await mcp.ListToolsAsync(cancellationToken: context.CancellationToken);
        Assert.Contains(tool => tool.Name == TraceTool, tools);
        var result = await mcp.CallToolAsync(McpProcess.ToolCall(TraceTool, new JsonObject { ["trace_id"] = traceId }),
            context.CancellationToken);
        Assert.IsFalse(result.IsError ?? false);
        Assert.IsNotNull(result.StructuredContent);
        var output = result.StructuredContent.Value;
        var report = Path.Combine(context.TestTempDirectory!, "mcp-trace.json");
        await File.WriteAllTextAsync(report, output.GetRawText(), context.CancellationToken);
        context.AddResultFile(report);
        Assert.AreEqual(traceId, output.GetProperty("trace").GetProperty("trace_id").GetString());
        var observed = Assert.ContainsSingle(output.GetProperty("trace").GetProperty("spans").EnumerateArray()
            .Where(span => span.GetProperty("span_id").GetString() == server.SpanId));
        Assert.AreEqual(server.ParentSpanId, observed.GetProperty("parent_span_id").GetString());
        Assert.AreEqual("GET /capture", observed.GetProperty("name").GetString());
        var compactRowServiceNotInstrumentationScope = observed.GetProperty("service").GetString();
        Assert.IsFalse(string.IsNullOrEmpty(compactRowServiceNotInstrumentationScope));
        var attributes = observed.GetProperty("attributes").Deserialize<Qyl.Api.Contracts.Common.Attribute[]>();
        Assert.IsNotNull(attributes);
        Assert.That.HasCapturedValues(attributes, CapturedKey, ProbeSignals.RequestId);
        Assert.IsFalse(output.GetProperty("has_more").GetBoolean());
        context.WriteLine($"Executed {format} application {artifact.Sha256} with native Collector {s_delivery.NativeCollector.Sha256}.");
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task PackagedTypedView_RequiredString_AcceptsAndExposesTheToolName()
    {
        var run = await RunSemanticProbeAsync(RequiredStringInput.String);
        using var report = run.Report;
        Assert.AreEqual(0, run.ExitCode, run.Error);
        Assert.IsFalse(report.RootElement.GetProperty("dynamicCodeSupported").GetBoolean());
        Assert.IsTrue(report.RootElement.GetProperty("created").GetBoolean());
        Assert.IsTrue(report.RootElement.GetProperty("hasView").GetBoolean());
        Assert.AreEqual("lookup_stock", report.RootElement.GetProperty("toolName").GetString());
    }

    [TestMethod]
    [DataRow(RequiredStringInput.Integer)]
    [DataRow(RequiredStringInput.Boolean)]
    [DataRow(RequiredStringInput.Null)]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task PackagedTypedView_WrongWireType_RejectsTheView(RequiredStringInput wireType)
    {
        var run = await RunSemanticProbeAsync(wireType);
        using var report = run.Report;
        Assert.AreEqual(0, run.ExitCode, run.Error);
        Assert.IsFalse(report.RootElement.GetProperty("dynamicCodeSupported").GetBoolean());
        Assert.IsFalse(report.RootElement.GetProperty("created").GetBoolean());
        Assert.IsFalse(report.RootElement.GetProperty("hasView").GetBoolean());
        Assert.IsFalse(report.RootElement.TryGetProperty("toolName", out _));
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task PackagedSignalMetadata_GenAiMainAgent_KeepsPhaseSamplingAndRegistryOwnership()
    {
        var run = await RunSemanticProbeAsync(RequiredStringInput.String);
        using var report = run.Report;
        Assert.AreEqual(0, run.ExitCode, run.Error);
        var value = report.RootElement;
        Assert.AreEqual("Start", value.GetProperty("methodPhase").GetString());
        Assert.IsTrue(value.GetProperty("methodSamplingRelevant").GetBoolean());
        Assert.AreEqual("End", value.GetProperty("statusPhase").GetString());
        Assert.IsFalse(value.GetProperty("statusSamplingRelevant").GetBoolean());
        Assert.AreEqual("Recommended", value.GetProperty("signalRequirement").GetString());
        Assert.AreEqual("gen_ai.main_agent", value.GetProperty("entityType").GetString());
        Assert.AreEqual("https://opentelemetry.io/schemas/gen-ai-dev/1.42.0-dev", value.GetProperty("registrySchema").GetString());
    }

    private static PublishedExecutable Application(PreparedDelivery delivery, ExecutableFormat format) => format switch
    {
        ExecutableFormat.ManagedAssembly => delivery.ManagedApplication,
        ExecutableFormat.NativeAot => delivery.NativeApplication,
        _ => throw new ArgumentOutOfRangeException(nameof(format))
    };

    private async Task<(int ExitCode, string Error, JsonDocument Report)> RunSemanticProbeAsync(RequiredStringInput input)
    {
        var delivery = s_delivery ?? throw new InvalidOperationException("Delivery artifacts have not been prepared.");
        context.AddResultFile(delivery.ManifestPath);
        var start = delivery.SemanticProbe.StartInfo();
        start.ArgumentList.Add(input switch
        {
            RequiredStringInput.String => "\"lookup_stock\"",
            RequiredStringInput.Integer => "42",
            RequiredStringInput.Boolean => "true",
            RequiredStringInput.Null => "null",
            _ => throw new ArgumentOutOfRangeException(nameof(input))
        });
        var (exitCode, output, error) = await ChildProcess.RunAsync(start, "the semantic contract consumer", context.CancellationToken,
            new ChildProcessOptions { ExecutionTimeout = TimeSpan.FromSeconds(30) });
        var diagnostics = Path.Combine(context.TestTempDirectory!, "semantic-consumer.log");
        await File.WriteAllTextAsync(diagnostics, $"{delivery.SemanticProbe.Path}\nSHA-256: {delivery.SemanticProbe.Sha256}\n{output}\n{error}", CancellationToken.None);
        context.AddResultFile(diagnostics);
        return (exitCode, error, JsonDocument.Parse(output));
    }

    public enum RequiredStringInput { String, Integer, Boolean, Null }
}
