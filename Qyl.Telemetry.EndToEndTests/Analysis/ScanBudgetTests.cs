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
    private static readonly SharedAnalysisSession s_shared = new();
    private static ulong s_since;

    private static AnalysisSession Session => s_shared.Session;

    [ClassInitialize]
    [Timeout(600_000, CooperativeCancellation = true)]
    public static async Task SeedAsync(TestContext context)
    {
        var session = await s_shared.StartAsync(context);
        s_since = Nanoseconds(session.TimeProvider.GetUtcNow()) - 40 * Minute;
        var incidents = new OtlpIncidents();
        incidents.Span("coverage-recurrence", Service, "checkout", s_since + Minute, error: true,
            attributes: new Dictionary<string, object> { ["error.type"] = "coverage" });
        for (var i = 0; i < Requests; i++)
        {
            incidents.Span($"coverage-request-{i}", Service, "checkout", s_since + 2 * Minute + (ulong)i * Millisecond);
            if (i % 5_000 == 4_999) await incidents.SendAsync(session.Client, context.CancellationToken);
        }
        await incidents.SendAsync(session.Client, context.CancellationToken);
    }

    [ClassCleanup]
    public static Task StopAsync() => s_shared.StopAsync();

    [TestMethod]
    [DataRow(AnalysisPath.Http)]
    [DataRow(AnalysisPath.Mcp)]
    public async Task VerifyFix_IncompleteScan_NeverPasses(AnalysisPath path)
    {
        var output = await Session.AskAsync<VerifyFixOutput>(path, AnalysisTools.VerifyFix, new JsonObject
        {
            ["start_time"] = AnswerKey.Iso(s_since - 5 * Minute), ["end_time"] = AnswerKey.Iso(s_since + 45 * Minute),
            ["since"] = AnswerKey.Iso(s_since),
            ["matchers"] = new JsonArray(AnswerKey.Matcher("service.name", "eq", Service))
        }, context.CancellationToken);
        Assert.AreNotEqual("passed", output.Verdict,
            $"A recurrence exists after the change; scan_complete={output.Coverage.ScanComplete}, requests={output.After.Requests}.");
    }
}
