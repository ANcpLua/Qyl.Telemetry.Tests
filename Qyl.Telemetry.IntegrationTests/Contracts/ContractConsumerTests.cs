using System.Text.Json;
using System.Text.Json.Nodes;
using Qyl.Api.Contracts;
using Qyl.Api.Contracts.Mcp.Tools;
using Qyl.Api.Contracts.OTel.Metrics;
using Qyl.Collector;
using Qyl.Collector.Analysis;
using Qyl.Collector.Hosting;
using Qyl.Collector.Storage;
using ContractJson = Qyl.Api.Contracts.ContractJson;
using MetricAggregation = Qyl.Api.Contracts.OTel.Metrics.MetricAggregation;

namespace Qyl.Telemetry.IntegrationTests.Contracts;

[TestClass]
public sealed class ContractConsumerTests(TestContext testContext)
{
    private const string AttributeKey = "feature.flag";
    private const string LeftTrace = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string RightTrace = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string PreciseTimestamp = "2026-10-23t12:34:56.123456789012+02:30";
    private static readonly DateTimeOffset s_now = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void CompareTraces_MissingVersusNullAttribute_ReportsPresentNullOnlyOnRecordedSide()
    {
        var result = Compare("{}", """{"feature.flag":null}""");
        var difference = result.FirstDivergence!;
        Assert.IsFalse(difference.A.IsDefined);
        Assert.IsTrue(difference.B.IsDefined);
        Assert.IsNull(difference.B.Value);
    }

    [TestMethod]
    public void CompareTraces_NullVersusMissingAttribute_ReportsPresentNullOnlyOnRecordedSide()
    {
        var difference = Compare("""{"feature.flag":null}""", "{}").FirstDivergence!;
        Assert.IsTrue(difference.A.IsDefined);
        Assert.IsNull(difference.A.Value);
        Assert.IsFalse(difference.B.IsDefined);
    }

    [TestMethod]
    public void CompareTraces_MissingVersusNullAttribute_PreservesPresenceInAlignedDifferences()
    {
        var aligned = Assert.ContainsSingle(Compare("{}", """{"feature.flag":null}""").Aligned);
        var difference = Assert.ContainsSingle(aligned.Differences);
        Assert.IsFalse(difference.A.IsDefined);
        Assert.IsTrue(difference.B.IsDefined);
        Assert.IsNull(difference.B.Value);
    }

    [TestMethod]
    public void CompareTraces_EqualNullAttributes_HasNoDivergence()
    {
        var result = Compare("""{"feature.flag":null}""", """{"feature.flag":null}""");
        Assert.IsNull(result.FirstDivergence);
    }

    [TestMethod]
    public void ContractHttp_InvalidTimestamp_RejectsResponseBeforeWriting()
    {
        var metric = Metric("2026-02-30T12:00:00Z");
        _ = Assert.ThrowsExactly<JsonException>(() => ContractHttp.Json(metric));
    }

    [TestMethod]
    public async Task ContractStream_PreciseTimestamp_PreservesOriginalText()
    {
        using var stream = new MemoryStream();
        await ContractJson.SerializeAsync(stream, Metric(PreciseTimestamp), testContext.CancellationToken);
        using var document = JsonDocument.Parse(stream.ToArray());
        Assert.AreEqual(PreciseTimestamp, document.RootElement.GetProperty("start_time").GetString());
    }

    [TestMethod]
    public void ContractDateTime_SubTickTimestamp_ValidatesWithoutSilentlyRoundingExecutionBoundary()
    {
        Assert.IsTrue(ContractDateTime.IsValid(PreciseTimestamp));
        var error = Assert.ThrowsExactly<AnalysisInputException>(() => J.Date(new JsonObject { ["start_time"] = PreciseTimestamp }, "start_time"));
        Assert.AreEqual("time.precision_unsupported", error.Code);
    }

    [TestMethod]
    public void ToolError_ExplicitNullRevision_HasDefinedOptionalValue()
    {
        var error = ContractJson.Deserialize<ToolError>("""{"code":"collector_contract_mismatch","message":"Mismatch","retryable":true,"collector_contract_revision":null}""")!;
        Assert.IsTrue(error.CollectorContractRevision.IsDefined);
        Assert.IsNull(error.CollectorContractRevision.Value);
    }

    [TestMethod]
    public void ToolError_AbsentRevision_HasUndefinedOptionalValue()
    {
        var error = ContractJson.Deserialize<ToolError>("""{"code":"internal","message":"Failure","retryable":false}""")!;
        Assert.IsFalse(error.CollectorContractRevision.IsDefined);
    }

    [TestMethod]
    public void MetricSeries_ExplicitNullAggregate_PreservesDefinedNull()
    {
        var series = new MetricSeriesResult { Attributes = [], Buckets = [], Value = new OptionalValue<double?>(null) };
        var wire = ContractJson.Serialize(series);
        Assert.AreEqual(JsonValueKind.Null, wire.GetProperty("value").ValueKind);
    }

    [TestMethod]
    public void MetricSeries_DefaultOptionalAggregate_OmitsValue()
    {
        var series = new MetricSeriesResult { Attributes = [], Buckets = [] };
        Assert.IsFalse(ContractJson.Serialize(series).TryGetProperty("value", out _));
    }

    [TestMethod]
    public void VerifyFix_NewFailureAfterChange_ProducesContractValidGuardrails()
    {
        var previous = Span(LeftTrace, "{}", minutesAgo: 10) with { Name = "fixed-operation", StatusCode = 2 };
        var introduced = Span(RightTrace, "{}") with { Name = "new-operation", StatusCode = 2 };
        var snapshot = new AnalysisSnapshot([previous, introduced], [], [], [], [], [], []);
        var engine = new AnalysisEngine(snapshot, "alpha", new AnalysisHandles(new byte[32]), s_now);
        var triage = engine.Run("triage", new JsonObject());
        var previousFailure = triage["failures"]!.AsArray()
            .Single(failure => failure!["operation"]!.GetValue<string>() == previous.Name)!;

        var output = engine.Run("verify_fix", new JsonObject
        {
            ["failure_id"] = previousFailure["failure_id"]!.GetValue<string>(),
            ["since"] = s_now.AddMinutes(-5).ToString("O")
        });
        var wire = ContractJson.Serialize("Mcp.Tools.VerifyFixOutput", ContractJson.ToElement(output));
        var result = ContractJson.Deserialize<VerifyFixOutput>(wire)!;
        var guardrail = Assert.ContainsSingle(result.Guardrails);
        Assert.AreEqual(introduced.Name, guardrail.Operation);
        Assert.AreEqual(FailureChange.New, guardrail.Change);
        Assert.AreEqual(1L, guardrail.Count);
        Assert.AreEqual(0L, guardrail.BaselineCount);
        Assert.AreEqual(RightTrace, guardrail.ExampleTraceId);
    }

    private static CompareTracesOutput Compare(string leftAttributes, string rightAttributes)
    {
        var snapshot = new AnalysisSnapshot([Span(LeftTrace, leftAttributes), Span(RightTrace, rightAttributes)], [], [], [], [], [], []);
        var input = new JsonObject
        {
            ["a"] = new JsonObject { ["trace_id"] = LeftTrace },
            ["b"] = new JsonObject { ["trace_id"] = RightTrace }
        };
        var output = new AnalysisEngine(snapshot, "alpha", new AnalysisHandles(new byte[32]), s_now).Run("compare_traces", input);
        var wire = ContractJson.Serialize("Mcp.Tools.CompareTracesOutput", ContractJson.ToElement(output));
        return ContractJson.Deserialize<CompareTracesOutput>(wire)!;
    }

    private static SpanStorageRow Span(string trace, string attributes, int minutesAgo = 1)
    {
        var start = (ulong)s_now.AddMinutes(-minutesAgo).ToUnixTimeMilliseconds() * TimeSpan.TicksPerMillisecond * TimeSpan.NanosecondsPerTick;
        return new SpanStorageRow
        {
            ProjectId = "alpha", TraceId = trace, SpanId = "1111111111111111", Name = "operation",
            Kind = 1, StartTimeUnixNano = start, EndTimeUnixNano = start + 1000, DurationNs = 1000,
            StatusCode = 1, ServiceName = "checkout", AttributesJson = attributes
        };
    }

    private static MetricQueryResult Metric(string timestamp) => new()
    {
        Name = AttributeKey, Kind = MetricKind.Gauge, Aggregation = MetricAggregation.Last,
        StepMs = 1000, StartTime = timestamp, EndTime = timestamp,
        NumericRepresentation = "float64", Approximate = false, Series = [], Truncated = false
    };
}
