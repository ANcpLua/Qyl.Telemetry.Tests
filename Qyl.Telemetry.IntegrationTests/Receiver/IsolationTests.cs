using System.Collections.Concurrent;
using System.Diagnostics;
using JetBrains.Annotations;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using SpanKind = Qyl.Telemetry.SemanticConventions.SpanKind;

namespace Qyl.Telemetry.IntegrationTests.Receiver;

[TracePerTest]
public sealed class IsolationTests
{
    private static readonly ConcurrentDictionary<string, byte> SeenTraceIds = new(StringComparer.Ordinal);

    [UsedImplicitly] public TestContext TestContext { get; set; } = null!;

    [ClassInitialize]
    public static void InitializeRows(TestContext context)
    {
        SeenTraceIds.Clear();
    }

    [ClassCleanup]
    public static void CleanupRows()
    {
        SeenTraceIds.Clear();
    }

    [TestMethod]
    [ConformsToSemanticConventions]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    public async Task Native_trace_scope_preserves_each_data_rows_identity(int row)
    {
        var root = Activity.Current;
        Assert.IsNotNull(root);
        Assert.AreEqual(TracePerTestAttribute.ActivityName, root.OperationName);
        Assert.IsTrue(SeenTraceIds.TryAdd(root.TraceId.ToHexString(), 0),
            "Each native DataRow must own a distinct trace ID.");
        Assert.AreEqual("0000000000000000", root.ParentSpanId.ToHexString());
        var capture = await TelemetryCapture.TraceAsync(async test =>
        {
            Assert.AreEqual(root.TraceId.ToHexString(), test.TraceId);
            using var source = new ActivitySource("isolated-" + Guid.NewGuid().ToString("N"));
            using var provider = Sdk.CreateTracerProviderBuilder().AddSource(source.Name)
                .SetResourceBuilder(ResourceBuilder.CreateEmpty().AddService("isolated"))
                .AddOtlpExporter(o =>
                {
                    o.Protocol = OtlpExportProtocol.HttpProtobuf;
                    o.Compression = OtlpExportCompression.GZip;
                    o.Endpoint = new Uri(test.Endpoint, "v1/traces");
                    o.ExportProcessorType = ExportProcessorType.Simple;
                }).Build();
            using var span = source.StartActivity("row." + row, ActivityKind.Client)!;
            string[] labels = ["first", "second"];
            span.SetStatus(ActivityStatusCode.Ok).SetTag("checkout.row", row)
                .SetTag("test.row", row).SetTag("checkout.accepted", false).SetTag("checkout.labels", labels);
            span.Stop();
            await test.AwaitAsync(s => s.Spans.Count == 1, TimeSpan.FromSeconds(10), TestContext.CancellationToken);
            Assert.AreEqual(test.TraceId, test.Read().TraceId);
            Assert.StartsWith("00-" + test.TraceId + "-", test.TraceParent);
        }, trace => trace.Spans.Count == 1, TimeSpan.FromSeconds(10), TestContext.CancellationToken);
        var observed = Assert.ContainsSingle(capture.Spans);
        Assert.AreEqual(root.TraceId.ToHexString(), observed.TraceId);
        Assert.AreEqual(root.SpanId.ToHexString(), observed.ParentSpanId);
        Assert.AreEqual("row." + row, observed.Name);
        Assert.AreEqual(SpanKind.Client, observed.Kind);
        Assert.AreEqual(ActivityStatusCode.Ok, observed.Status);
        Assert.StartsWith("isolated-", observed.Scope?.Name);
        Assert.AreEqual(row, observed.GetInt64("checkout.row"));
        Assert.AreEqual(row, observed.GetInt64("test.row"));
        Assert.IsFalse(observed.GetBoolean("checkout.accepted"));
        Assert.AreSequenceEqual(["first", "second"], observed.GetStrings("checkout.labels")!);
    }

    [TestMethod]
    public async Task Action_failure_is_preserved_and_the_receiver_is_closed()
    {
        Uri? endpoint = null;
        var failure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => TelemetryCapture.TraceAsync(
            test =>
            {
                endpoint = test.Endpoint;
                throw new InvalidOperationException("business failure");
            }, _ => false,
            cancellationToken: TestContext.CancellationToken));
        Assert.AreEqual("business failure", failure.Message);
        Assert.IsNotNull(endpoint);
        var client = TelemetryLifecycle.Http;
        await Assert.ThrowsExactlyAsync<HttpRequestException>(() =>
            client.GetAsync(endpoint, TestContext.CancellationToken));
    }
}
