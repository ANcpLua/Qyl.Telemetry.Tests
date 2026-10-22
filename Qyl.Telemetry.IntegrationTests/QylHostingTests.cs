using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Exporter;

namespace Qyl.Telemetry.IntegrationTests;

[TestClass]
public sealed class QylHostingTests(TestContext context)
{
    private const string ServiceName = "qyl-hosting-fixture";
    private const string SourceName = "Telemetry.Hosting.Fixture";
    private const string OperationName = "fixture.completed";
    private const string LabelsKey = "fixture.labels";
    private const string FirstLabel = "first";
    private const string SecondLabel = "second";

    [TestMethod]
    public async Task QylHosting_PublicHostSetup_ExportsCompletedTypedEvidenceBeforeDisposalReturns()
    {
        await using var receiver = await OtlpReceiver.StartAsync(context.CancellationToken);
        var endpoint = receiver.Endpoint;
        var builder = Host.CreateApplicationBuilder();
        builder.AddQyl(options =>
        {
            options.ServiceName = ServiceName;
            options.CollectorEndpoint = endpoint;
            options.CollectorProtocol = OtlpExportProtocol.HttpProtobuf;
            options.AdditionalSources.Add(SourceName);
        });
        string traceId;
        using (var host = builder.Build())
        {
            await host.StartAsync(context.CancellationToken);
            using (var source = new ActivitySource(SourceName))
            using (var activity = source.CreateActivity(OperationName, ActivityKind.Internal))
            {
                Assert.IsNotNull(activity);
                activity.Start();
                traceId = activity.TraceId.ToHexString();
                string[] values = [FirstLabel, SecondLabel];
                activity.SetTag(LabelsKey, values);
            }
            await host.StopAsync(context.CancellationToken);
        }

        var snapshot = await receiver.AwaitAsync(
            evidence => evidence.Spans.Any(span => span.TraceId == traceId),
            TimeSpan.FromSeconds(10), context.CancellationToken);
        var span = Assert.ContainsSingle(snapshot.Spans.Where(span => span.TraceId == traceId));
        Assert.AreEqual(OperationName, span.Name);
        Assert.AreEqual(SourceName, span.Scope?.Name);
        Assert.AreEqual(ServiceName, span.Resource.Attributes["service.name"]);
        var labels = Assert.IsInstanceOfType<IReadOnlyList<object?>>(span.Attributes[LabelsKey]);
        Assert.AreSequenceEqual([FirstLabel, SecondLabel], labels);
        Assert.IsNotEmpty(snapshot.Requests);
        Assert.IsTrue(snapshot.Receipts.All(receipt => receipt is { Rejected: 0, Error: null }));
    }
}
