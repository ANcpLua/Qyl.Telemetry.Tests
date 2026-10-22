using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Qyl.Telemetry.SemanticConventions.Attributes.Http;
using Qyl.Telemetry.Tests.Collector;
using SpanKind = Qyl.Telemetry.SemanticConventions.SpanKind;

namespace Qyl.Telemetry.EndToEndTests.Delivery;

[TracePerTest]
public sealed class DeliveryTests(TestContext context)
{
    private const string TraceTool = "get_trace";
    private const string CapturedKey = HttpAttributes.RequestHeader + ".x-probe-id";
    private static PreparedDelivery? _delivery;

    [ClassInitialize]
    [Timeout(1_800_000, CooperativeCancellation = true)]
    public static async Task PublishArtifactsAsync(TestContext context) =>
        _delivery = await new DeliveryArtifacts(context).PrepareAsync();

    [TestMethod]
    [ConnectedTrace]
    [NoDuplicateSpans]
    [PreservedByCollector]
    [DataRow(ExecutableFormat.ManagedAssembly)]
    [DataRow(ExecutableFormat.NativeAot)]
    [Timeout(180_000, CooperativeCancellation = true)]
    public async Task PublishedPackages_ManagedOrNativeAotApplication_ReachTheNativeCollectorAndRealMcp(ExecutableFormat format)
    {
        Assert.IsNotNull(_delivery);
        context.AddResultFile(_delivery.ManifestPath);
        var artifact = format == ExecutableFormat.NativeAot ? _delivery.NativeApplication : _delivery.ManagedApplication;
        await using var application = await InstrumentedApplicationFixture.StartAsync(artifact, _delivery.NativeCollector,
            context, new Dictionary<string, string>
            {
                ["OTEL_DOTNET_AUTO_TRACES_ASPNETCORE_INSTRUMENTATION_CAPTURE_REQUEST_HEADERS"] = ProbeSignals.RequestHeader
            });
        using var root = application.Telemetry.Begin(context.TestName);
        var traceId = root.TraceId.ToHexString();
        using (application.Telemetry.Ending(root))
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "capture");
            request.Headers.Add(ProbeSignals.RequestHeader, ProbeSignals.RequestId);
            using var response = await application.Client.SendAsync(request, context.CancellationToken);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(context.CancellationToken));
            Assert.AreEqual(format == ExecutableFormat.ManagedAssembly, body.RootElement.GetProperty("isDynamicCodeSupported").GetBoolean());
        }

        var capture = await application.Telemetry.AwaitAsync(snapshot => snapshot.Spans.Any(span =>
            span.TraceId == traceId && span is { Kind: SpanKind.Server, Service: "telemetry-probe" }), context.CancellationToken);
        var trace = new TraceEvidence(traceId, capture);
        var server = Assert.HasSingleNativeSpan(trace.Spans.Where(span => span.Service == "telemetry-probe"),
            "Microsoft.AspNetCore", "GET /capture", SpanKind.Server);
        var client = Assert.ContainsSingle(trace.Spans.Where(span => span.SpanId == server.ParentSpanId));
        Assert.AreEqual(SpanKind.Client, client.Kind);
        Assert.HasParentContext(server, client);
        Assert.HasParentContext(client, root.Context);
        Assert.AreSequenceEqual([ProbeSignals.RequestId], server.GetStrings(CapturedKey)!);
        _ = await application.Telemetry.Collector.Client.WaitForTraceAsync(traceId,
            stored => stored.GetProperty("spans").EnumerateArray().Any(span => span.GetProperty("span_id").GetString() == server.SpanId),
            context.CancellationToken);

        await using var mcp = await PublishedMcp.ConnectAsync(_delivery.McpEntryPoint, application.Telemetry.Collector.Address,
            line => context.WriteLine("MCP: " + line), context.CancellationToken);
        var tools = await mcp.ListToolsAsync(cancellationToken: context.CancellationToken);
        Assert.Contains(tool => tool.Name == TraceTool, tools);
        var result = await mcp.CallToolAsync(PublishedMcp.ToolCall(TraceTool, new JsonObject { ["trace_id"] = traceId }),
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
        Assert.HasCapturedValues(attributes, CapturedKey, ProbeSignals.RequestId);
        Assert.IsFalse(output.GetProperty("has_more").GetBoolean());
        context.WriteLine($"Executed {format} application {artifact.Sha256} with native Collector {_delivery.NativeCollector.Sha256}.");
    }

    [TestMethod]
    [DataRow(RequiredStringInput.String)]
    [DataRow(RequiredStringInput.Integer)]
    [DataRow(RequiredStringInput.Boolean)]
    [DataRow(RequiredStringInput.Null)]
    public async Task PackagedTypedView_RequiredStringInput_AcceptsAStringAndRejectsWrongWireTypes(RequiredStringInput input)
    {
        using var report = await RunSemanticProbeAsync(input);
        var expected = input == RequiredStringInput.String;
        Assert.IsFalse(report.RootElement.GetProperty("dynamicCodeSupported").GetBoolean());
        Assert.AreEqual(expected, report.RootElement.GetProperty("created").GetBoolean());
        Assert.AreEqual(expected, report.RootElement.GetProperty("hasView").GetBoolean());
        if (expected) Assert.AreEqual("lookup_stock", report.RootElement.GetProperty("toolName").GetString());
        else Assert.IsFalse(report.RootElement.TryGetProperty("toolName", out _));
    }

    [TestMethod]
    public async Task PackagedSignalMetadata_GenAiMainAgent_KeepsPhaseSamplingAndRegistryOwnership()
    {
        using var report = await RunSemanticProbeAsync(RequiredStringInput.String);
        var value = report.RootElement;
        Assert.AreEqual("Start", value.GetProperty("methodPhase").GetString());
        Assert.IsTrue(value.GetProperty("methodSamplingRelevant").GetBoolean());
        Assert.AreEqual("End", value.GetProperty("statusPhase").GetString());
        Assert.IsFalse(value.GetProperty("statusSamplingRelevant").GetBoolean());
        Assert.AreEqual("Recommended", value.GetProperty("signalRequirement").GetString());
        Assert.AreEqual("gen_ai.main_agent", value.GetProperty("entityType").GetString());
        Assert.AreEqual("https://opentelemetry.io/schemas/gen-ai-dev/1.42.0-dev", value.GetProperty("registrySchema").GetString());
    }

    private async Task<JsonDocument> RunSemanticProbeAsync(RequiredStringInput input)
    {
        Assert.IsNotNull(_delivery);
        context.AddResultFile(_delivery.ManifestPath);
        var start = _delivery.SemanticProbe.StartInfo();
        start.ArgumentList.Add(input switch
        {
            RequiredStringInput.String => "\"lookup_stock\"",
            RequiredStringInput.Integer => "42",
            RequiredStringInput.Boolean => "true",
            RequiredStringInput.Null => "null",
            _ => throw new ArgumentOutOfRangeException(nameof(input))
        });
        var (exitCode, output, error) = await ChildProcess.RunAsync(start, "the semantic contract consumer", context.CancellationToken);
        Assert.AreEqual(0, exitCode, error);
        return JsonDocument.Parse(output);
    }

    public enum RequiredStringInput { String, Integer, Boolean, Null }
}
