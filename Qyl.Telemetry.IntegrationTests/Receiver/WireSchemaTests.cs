using System.Diagnostics;
using JetBrains.Annotations;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Qyl.Telemetry.SemanticConventions.Attributes.Http;
using Qyl.Telemetry.SemanticConventions.Attributes.Service;
using SpanKind = Qyl.Telemetry.SemanticConventions.SpanKind;

namespace Qyl.Telemetry.IntegrationTests.Receiver;

[TestClass]
public sealed class WireSchemaTests
{
    [UsedImplicitly] public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DataRow(null)]
    [DataRow("https://opentelemetry.io/schemas/1.44.0")]
    public async Task Resource_schema_survives_the_official_sdk_and_protobuf_wire(string? schemaUrl)
    {
        await using var receiver = await OtlpReceiver.StartAsync(TestContext.CancellationToken);
        using var source = new ActivitySource("wire-" + Guid.NewGuid().ToString("N"), "1.2.3");
        var sourceName = source.Name;
#pragma warning disable QYL0006 // Deliberately misleading attribute must not replace the actual resource schema URL.
        var resource = ResourceBuilder.CreateEmpty().AddAttributes(
            new Dictionary<string, object>
                { [ServiceAttributes.Name] = "schema-producer", ["telemetry.schema_url"] = "https://attribute.invalid/" },
            schemaUrl);
#pragma warning restore QYL0006
        var endpoint = new Uri(receiver.Endpoint, "v1/traces");
        using var provider = Sdk.CreateTracerProviderBuilder().SetResourceBuilder(resource).AddSource(sourceName)
            .AddOtlpExporter(options =>
            {
                options.Protocol = OtlpExportProtocol.HttpProtobuf;
                options.Compression = OtlpExportCompression.GZip;
                options.Endpoint = endpoint;
                options.ExportProcessorType = ExportProcessorType.Simple;
            }).Build();
        using var activity = source.CreateActivity("wire.operation", ActivityKind.Server)!.Start();
        activity.SetStatus(ActivityStatusCode.Ok).SetTag(HttpAttributes.RequestMethod, "POST");
        var traceId = activity.TraceId.ToHexString();
        activity.Stop();
        var capture = await receiver.AwaitAsync(s => s.Spans.Count == 1, TimeSpan.FromSeconds(10),
            TestContext.CancellationToken);
        var span = Assert.ContainsSingle(capture.Spans);
        Assert.AreEqual(traceId, span.TraceId);
        Assert.AreEqual("wire.operation", span.Name);
        Assert.AreEqual(SpanKind.Server, span.Kind);
        Assert.AreEqual(ActivityStatusCode.Ok, span.Status);
        Assert.AreEqual(source.Name, span.Scope?.Name);
        Assert.AreEqual("1.2.3", span.Scope?.Version);
        Assert.AreEqual(schemaUrl, span.Resource.SchemaUrl);
        Assert.AreEqual("https://attribute.invalid/", span.Resource.Attributes["telemetry.schema_url"]);
        Assert.AreEqual("schema-producer", span.Service);
        Assert.AreEqual("POST", span.GetString("http.request.method"));
    }
}
