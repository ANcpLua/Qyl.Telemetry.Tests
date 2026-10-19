using System.Diagnostics;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Qyl.Telemetry.Tests;
using Qyl.Telemetry.Tests.Policies;

namespace Qyl.Telemetry.IntegrationTests.Probes;

[TracePerTest]
[ExportsCleanly]
public sealed class PolicyCases(TestContext context)
{
    [TestMethod]
    [ConnectedTrace]
    [NoDuplicateSpans]
    [DataRow(PolicyContract.FirstRow)]
    [DataRow(PolicyContract.SecondRow)]
    public Task SatisfiedPoliciesKeepEachRowPassing(string row) =>
        ExportAsync(source => source.CreateActivity(row, ActivityKind.Server)?.Start().Dispose());

    [TestMethod]
    [NoDuplicateSpans]
    public Task WrappedOperationFails() => ExportAsync(source =>
    {
        using var native = source.CreateActivity(PolicyContract.Operation, ActivityKind.Server)?.Start();
        using var wrapper = source.CreateActivity(PolicyContract.Operation, ActivityKind.Server)?.Start();
    });

    [TestMethod]
    [ConnectedTrace]
    public void MissingEvidenceFails()
    {
    }

    [TestMethod]
    [ConnectedTrace]
    public void OriginalFailureIsKept() => Assert.Fail(PolicyContract.OriginalMessage);

    private async Task ExportAsync(Action<ActivitySource> operation)
    {
        var name = "policy-" + Guid.NewGuid().ToString("N");
        _ = await TelemetryCapture.TraceAsync(capture =>
        {
            using var source = new ActivitySource(name);
            using var provider = Sdk.CreateTracerProviderBuilder().AddSource(name)
                .SetResourceBuilder(ResourceBuilder.CreateEmpty().AddService("policy-probe"))
                .AddOtlpExporter(options =>
                {
                    options.Protocol = OtlpExportProtocol.HttpProtobuf;
                    options.Compression = OtlpExportCompression.GZip;
                    options.Endpoint = new Uri(capture.Endpoint, "v1/traces");
                    options.ExportProcessorType = ExportProcessorType.Simple;
                }).Build();
            operation(source);
            return Task.CompletedTask;
        }, trace => trace.Spans.Count > 0, cancellationToken: context.CancellationToken).ConfigureAwait(false);
    }
}

[TestClass]
public sealed class UnverifiedPolicyCases(TestContext context)
{
    [TestMethod]
    [ConnectedTrace]
    public async Task PlainTestClassRejectsPolicies() => _ = await TelemetryCapture.TraceAsync(_ => Task.CompletedTask,
        _ => true, cancellationToken: context.CancellationToken).ConfigureAwait(false);
}
