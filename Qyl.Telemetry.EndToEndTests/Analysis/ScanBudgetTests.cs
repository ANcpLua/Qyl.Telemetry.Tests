using System.Text.Json.Nodes;
using Qyl.Api.Contracts.Mcp.Tools;
using static Qyl.Telemetry.EndToEndTests.Analysis.OtlpIncidents;

namespace Qyl.Telemetry.EndToEndTests.Analysis;

/// <summary>A recurrence hidden beyond the analysis scan budget never lets verify_fix pass.</summary>
[TestClass]
[DoNotParallelize]
public sealed class ScanBudgetTests(TestContext context)
{
    private const string Service = "coverage-checkout";
    private const int Requests = 25_001;
    private static AnalysisSession? _session;
    private static ulong _since;

    private static AnalysisSession Session => _session ?? throw new InvalidOperationException("The Collector has not started.");

    [ClassInitialize]
    [Timeout(600_000, CooperativeCancellation = true)]
    public static async Task SeedAsync(TestContext context)
    {
        _session = await AnalysisSession.StartAsync(context);
        _since = Nanoseconds(_session.TimeProvider.GetUtcNow()) - 40 * Minute;
        var incidents = new OtlpIncidents();
        incidents.Span("coverage-recurrence", Service, "checkout", _since + Minute, error: true,
            attributes: new Dictionary<string, object> { ["error.type"] = "coverage" });
        for (var i = 0; i < Requests; i++)
        {
            incidents.Span($"coverage-request-{i}", Service, "checkout", _since + 2 * Minute + (ulong)i * Millisecond);
            if (i % 5_000 == 4_999) await incidents.SendAsync(_session.Client, context.CancellationToken);
        }
        await incidents.SendAsync(_session.Client, context.CancellationToken);
    }

    [ClassCleanup]
    public static async Task StopAsync()
    {
        if (_session is not null) await _session.DisposeAsync();
    }

    [TestMethod]
    [DataRow(AnalysisPath.Http)]
    [DataRow(AnalysisPath.Mcp)]
    public async Task VerifyFix_IncompleteScan_NeverPasses(AnalysisPath path)
    {
        var output = await Session.AskAsync<VerifyFixOutput>(path, "verify_fix", new JsonObject
        {
            ["start_time"] = AnswerKey.Iso(_since - 5 * Minute), ["end_time"] = AnswerKey.Iso(_since + 45 * Minute),
            ["since"] = AnswerKey.Iso(_since),
            ["matchers"] = new JsonArray(new JsonObject { ["key"] = "service.name", ["op"] = "eq", ["value"] = Service, ["scope"] = "resource" })
        }, context.CancellationToken);
        Assert.AreNotEqual("passed", output.Verdict,
            $"A recurrence exists after the change; scan_complete={output.Coverage.ScanComplete}, requests={output.After.Requests}.");
    }
}
