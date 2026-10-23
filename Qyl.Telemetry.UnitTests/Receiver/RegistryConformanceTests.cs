using Qyl.Telemetry.SemanticConventions;

namespace Qyl.Telemetry.UnitTests.Receiver;

[TestClass]
[TestCategory("Fast")]
public class RegistryConformanceTests
{
    [TestMethod]
    public void Should_IdentifyThePinnedRegistry_When_ReportingConformance()
    {
        Assert.AreEqual("https://qyl.at/schemas/15.0.5", EmptyReport().RegistrySchemaUrl.AbsoluteUri);
    }

    [TestMethod]
    public void Should_IdentifyTheUpstreamSchema_When_ReportingConformance()
    {
        Assert.AreEqual("https://opentelemetry.io/schemas/1.44.0", EmptyReport().UpstreamSchemaUrl.AbsoluteUri);
    }

    [TestMethod]
    public void Should_IdentifyTheResolvedRegistrySnapshot_When_ReportingConformance()
    {
        Assert.AreEqual("2798e34f623307847e2a00b9384e780aad5543eebf09f500f188519f1a2baa7b", EmptyReport().RegistrySha256);
    }

    [TestMethod]
    public void Should_ThrowArgumentNullException_When_RegistryAudit_NullCapture()
    {
        _ = Assert.ThrowsExactly<ArgumentNullException>(() => RegistryConformance.Check(null!));
    }

    [TestMethod]
    public void Should_BeNotConformant_When_RegistryAudit_EmptyCapture()
    {
        Assert.IsFalse(EmptyReport().IsConformant);
    }

    [TestMethod]
    public void Should_CheckNoRecord_When_RegistryAudit_EmptyCapture()
    {
        Assert.AreEqual(0, EmptyReport().RecordsChecked);
    }

    [TestMethod]
    public void Should_CheckNoAttribute_When_RegistryAudit_EmptyCapture()
    {
        Assert.AreEqual(0, EmptyReport().AttributesChecked);
    }

    [TestMethod]
    public void Should_CheckNoMetric_When_RegistryAudit_EmptyCapture()
    {
        Assert.AreEqual(0, EmptyReport().MetricsChecked);
    }

    [TestMethod]
    public void Should_ReportNoIssue_When_RegistryAudit_EmptyCapture()
    {
        Assert.IsEmpty(EmptyReport().Issues);
    }

    [TestMethod]
    public void Should_BeConformant_When_RegistryAudit_CustomNamesAndOpenEnumValues()
    {
        Assert.IsTrue(CustomReport().IsConformant);
    }

    [TestMethod]
    public void Should_BeBothCheckedAsRecords_When_RegistryAudit_CustomSpanAndMetric()
    {
        Assert.AreEqual(2, CustomReport().RecordsChecked);
    }

    [TestMethod]
    public void Should_BeTheOnlyCheckedAttribute_When_RegistryAudit_OpenEnumValue()
    {
        Assert.AreEqual(1, CustomReport().AttributesChecked);
    }

    [TestMethod]
    public void Should_BeNotCheckedAgainstTheRegistry_When_RegistryAudit_CustomMetric()
    {
        Assert.AreEqual(0, CustomReport().MetricsChecked);
    }

    [TestMethod]
    public void Should_BeListedAsCustom_When_RegistryAudit_CustomAttribute()
    {
        Assert.AreSequenceEqual(["custom.payload"], CustomReport().CustomAttributes);
    }

    [TestMethod]
    public void Should_BeListedAsCustom_When_RegistryAudit_CustomMetric()
    {
        Assert.AreSequenceEqual(["custom.metric"], CustomReport().CustomMetrics);
    }

    [TestMethod]
    public void Should_BeNotConformant_When_RegistryAudit_InvalidAttributesOnEveryRecordPart()
    {
        Assert.IsFalse(RegistryConformance.Check(EveryRecordPartCapture()).IsConformant);
    }

    [TestMethod]
    public void Should_BeNotCountedAsAnAttributeCollection_When_RegistryAudit_LogBodyMap()
    {
        Assert.AreEqual(8, RegistryConformance.Check(EveryRecordPartCapture()).AttributesChecked);
    }

    [TestMethod]
    public void Should_BeLocated_When_RegistryAudit_SpanLogResourceScopeEventAndLinkAttributes()
    {
        Assert.AreSequenceEqual(
        [
            "spans[0].resource", "spans[0].scope", "spans[0]", "spans[0].events[0]", "spans[0].links[0]",
            "logs[0].resource", "logs[0].scope", "logs[0]"
        ], RegistryConformance.Check(EveryRecordPartCapture()).Issues.Select(i => i.Location));
    }

    [TestMethod]
    public void Should_BeAnIntegerTypeViolation_When_RegistryAudit_StringStatusCodeOnEveryRecordPart()
    {
        Assert.IsTrue(RegistryConformance.Check(EveryRecordPartCapture()).Issues.All(i => i is
            { Name: "http.response.status_code", Message: "Expected Integer.", Level: RegistryIssueLevel.Violation }));
    }

    [TestMethod]
    public void Should_BeLeftUnchanged_When_RegistryAudit_LogBodyMap()
    {
        var capture = EveryRecordPartCapture();
        var body = capture.Logs[0].Body;
        _ = RegistryConformance.Check(capture);
        Assert.AreSame(body, capture.Logs[0].Body);
    }

    [TestMethod]
    public void Should_KeepZeroTime_When_RegistryAudit_LogWithoutTimestamp()
    {
        var capture = EveryRecordPartCapture();
        _ = RegistryConformance.Check(capture);
        Assert.AreEqual(0UL, capture.Logs[0].TimeUnixNano);
    }

    [TestMethod]
    public void Should_BeNotConformant_When_RegistryAudit_InvalidPointAttributes()
    {
        Assert.IsFalse(RegistryConformance.Check(EveryPointShapeCapture()).IsConformant);
    }

    [TestMethod]
    public void Should_BeCheckedAsAttributes_When_RegistryAudit_EveryPointShapeAndFilteredExemplar()
    {
        Assert.AreEqual(9, RegistryConformance.Check(EveryPointShapeCapture()).AttributesChecked);
    }

    [TestMethod]
    public void Should_BeLocated_When_RegistryAudit_EveryPointShapeAndFilteredExemplarAttributes()
    {
        Assert.AreSequenceEqual(
            [
                "metrics[0].points[0]", "metrics[0].points[0].exemplars[0]", "metrics[1].points[0]",
                "metrics[1].points[0].exemplars[0]",
                "metrics[2].points[0]", "metrics[2].points[0].exemplars[0]", "metrics[3].points[0]",
                "metrics[3].points[0].exemplars[0]", "metrics[4].points[0]"
            ],
            RegistryConformance.Check(EveryPointShapeCapture()).Issues.Select(i => i.Location));
    }

    [TestMethod]
    public void Should_BeEachChecked_When_RegistryAudit_RegisteredMetrics()
    {
        Assert.AreEqual(3, UnitsReport().MetricsChecked);
    }

    [TestMethod]
    public void Should_BeChecked_When_RegistryAudit_DeprecatedSpanAttribute()
    {
        Assert.AreEqual(1, UnitsReport().AttributesChecked);
    }

    [TestMethod]
    public void Should_BeReportedWithoutInferringAnSdkInstrument_When_RegistryAudit_DeprecationsAndUnitMismatch()
    {
        Assert.AreSequenceEqual(["db.system", "http.server.request.duration", "container.memory.paging.faults"],
            UnitsReport().Issues.Select(i => i.Name));
    }

    [TestMethod]
    public void Should_NameExpectedAndObservedUnit_When_RegistryAudit_UnitMismatch()
    {
        Assert.AreEqual("Expected unit 's', observed 'ms'.", UnitsReport().Issues[1].Message);
    }

    [TestMethod]
    public void Should_BeLocatedOnItsMetric_When_RegistryAudit_UnitMismatch()
    {
        Assert.AreEqual("metrics[1]", UnitsReport().Issues[1].Location);
    }

    [TestMethod]
    public void Should_BeNotConformant_When_RegistryAudit_UnitMismatch()
    {
        Assert.IsFalse(UnitsReport().IsConformant);
    }

    [TestMethod]
    public void Should_BeNotAProtocolFailure_When_RegistryAudit_Deprecation()
    {
        Assert.IsTrue(DeprecationReport().IsConformant);
    }

    [TestMethod]
    public void Should_BeAnImprovement_When_RegistryAudit_Deprecation()
    {
        Assert.AreEqual(RegistryIssueLevel.Improvement, Assert.ContainsSingle(DeprecationReport().Issues).Level);
    }

    [TestMethod]
    public void Should_NameTheAttribute_When_RegistryAudit_Deprecation()
    {
        Assert.AreEqual("db.system", Assert.ContainsSingle(DeprecationReport().Issues).Name);
    }

    [TestMethod]
    public void Should_BeLocatedOnItsSpan_When_RegistryAudit_Deprecation()
    {
        Assert.AreEqual("spans[0]", Assert.ContainsSingle(DeprecationReport().Issues).Location);
    }

    [TestMethod]
    public void Should_SayTheRegistryDeprecatesIt_When_RegistryAudit_Deprecation()
    {
        Assert.AreEqual("The pinned registry marks this attribute deprecated.", Assert.ContainsSingle(DeprecationReport().Issues).Message);
    }

    [TestMethod]
    public void Should_BeConformant_When_RegistryAudit_TemplateAttributesWithDeclaredArrayKind()
    {
        Assert.IsTrue(TemplateReport().IsConformant);
    }

    [TestMethod]
    public void Should_BeCheckedButCustomNeighborIsNot_When_RegistryAudit_TemplateAttributes()
    {
        Assert.AreEqual(3, TemplateReport().AttributesChecked);
    }

    [TestMethod]
    public void Should_BeListedAsCustom_When_RegistryAudit_TemplateLookalikeNeighbor()
    {
        Assert.AreSequenceEqual(["http.request.headers.content_type"], TemplateReport().CustomAttributes);
    }

    [TestMethod]
    public void Should_NotReportATypeViolation_When_RegistryAudit_ContextDependentCodexThreadId()
    {
        Assert.IsEmpty(ContextDependentReport("0199a213-81c0-7800-8aa1-bbab2a035a53").Issues
            .Where(issue => issue.Level == RegistryIssueLevel.Violation));
    }

    [TestMethod]
    public void Should_ReportAnUnevaluatedType_When_RegistryAudit_ContextDependentCodexThreadId()
    {
        Assert.AreEqual(new RegistryIssue("spans[0]", "thread.id", RegistryIssueLevel.Unevaluated,
                "The registered value type depends on the selected signal definition; run explicit definition conformance."),
            Assert.ContainsSingle(ContextDependentReport("0199a213-81c0-7800-8aa1-bbab2a035a53").Issues));
    }

    [TestMethod]
    [DataRow("0199a213-81c0-7800-8aa1-bbab2a035a53")]
    [DataRow(7L)]
    [DataRow(true)]
    public void Should_NotClaimConformance_When_RegistryAudit_ContextDependentTypeHasNoSelectedDefinition(object threadId)
    {
        Assert.IsFalse(ContextDependentReport(threadId).IsConformant);
    }

    [TestMethod]
    public void Should_CountTheRegisteredAttribute_When_RegistryAudit_ContextDependentCodexThreadId()
    {
        Assert.AreEqual(1, ContextDependentReport("0199a213-81c0-7800-8aa1-bbab2a035a53").AttributesChecked);
    }

    [TestMethod]
    public void Should_NotClassifyTheAttributeAsCustom_When_RegistryAudit_ContextDependentCodexThreadId()
    {
        Assert.IsEmpty(ContextDependentReport("0199a213-81c0-7800-8aa1-bbab2a035a53").CustomAttributes);
    }

    [TestMethod]
    public void Should_PreserveTheCoreIntegerKind_When_RegistryAttributeHasAContextualStringDefinition()
    {
        Assert.IsTrue(RegistryCatalog.TryGetAttribute("thread.id", out var attribute));
        Assert.IsNotNull(attribute);
        Assert.AreEqual(RegistryAttributeValueKind.Integer, attribute.ValueKind);
    }

    [TestMethod]
    public void Should_RequireRegeneration_When_TypeContextWasGeneratedForAnotherRegistry()
    {
        var error = Assert.ThrowsExactly<InvalidOperationException>(() =>
            RegistryConformance.VerifyRegistrySnapshot("different-snapshot"));
        Assert.AreEqual(
            $"Registry type-context metadata was generated for registry '{RegistryTypeContext.RegistrySha256}', "
            + "but the loaded registry is 'different-snapshot'. "
            + "Regenerate it with python3 eng/generate-registry-type-context.py.", error.Message);
    }

    [TestMethod]
    [DynamicData(nameof(ValueCases))]
    public void Should_PreserveOtlpTypes_When_RegistryAudit_ValueKinds(RegistryAttributeValueKind kind, object? attributeValue, bool expected)
    {
        Assert.AreEqual(expected, RegistryConformance.Matches(kind, attributeValue));
    }

    public static IEnumerable<object?[]> ValueCases()
    {
        yield return [RegistryAttributeValueKind.Unknown, "unrecognized", false];
        yield return [RegistryAttributeValueKind.String, "text", true];
        yield return [RegistryAttributeValueKind.String, null, false];
        yield return [RegistryAttributeValueKind.Integer, long.MaxValue, true];
        yield return [RegistryAttributeValueKind.Integer, 1.0, false];
        yield return [RegistryAttributeValueKind.Double, 1.0, true];
        yield return [RegistryAttributeValueKind.Double, 1L, false];
        yield return [RegistryAttributeValueKind.Boolean, true, true];
        yield return [RegistryAttributeValueKind.Boolean, "true", false];
        yield return [RegistryAttributeValueKind.Any, new byte[] { 1 }, true];
        foreach (var (kind, valid, wrong) in new[]
                 {
                     (RegistryAttributeValueKind.StringArray, (object)"text", (object)1L),
                     (RegistryAttributeValueKind.IntegerArray, 1L, "text"),
                     (RegistryAttributeValueKind.DoubleArray, 1.0, 1L),
                     (RegistryAttributeValueKind.BooleanArray, true, "text")
                 })
        {
            yield return [kind, new[] { valid }, true];
            yield return [kind, new[] { valid, wrong }, false];
            yield return [kind, valid, false];
            yield return [kind, Array.Empty<object?>(), true];
        }
    }

    private static RegistryReport EmptyReport() => RegistryConformance.Check(TestSpan.Only());

    private static RegistryReport CustomReport()
    {
        var span = new SpanRecord("trace", "span", "custom")
        {
            Attributes = Attributes(("error.type", "application-specific"), ("custom.payload", new byte[] { 1, 2 }))
        };
        var metric = Metric("custom.metric", "widget", new UnspecifiedMetricData());
        return RegistryConformance.Check(new TelemetrySnapshot([span], [], [metric], []));
    }

    private static TelemetrySnapshot EveryRecordPartCapture()
    {
        var invalid = Attributes(("http.response.status_code", "500"));
        var scope = new InstrumentationScopeRecord { Attributes = invalid };
        var resource = new ResourceRecord(invalid);
        var span = new SpanRecord("trace", "span", "custom")
        {
            Resource = resource, Scope = scope, Attributes = invalid,
            Events = [new SpanEventRecord("custom.event", 1, invalid)],
            Links = [new SpanLinkRecord("other-trace", "other-span", invalid)]
        };
        var log = new LogRecord(resource, scope, 0, 0, 17, "Error", invalid, invalid, 0, 0, "trace", "span",
            "custom.event");
        return new TelemetrySnapshot([span], [log], [], []);
    }

    private static TelemetrySnapshot EveryPointShapeCapture()
    {
        var invalid = Attributes(("http.response.status_code", "500"));
        var exemplar = new ExemplarRecord(invalid, 1, 3L, "trace", "span");
        var number = new NumberPoint(invalid, 0, 1, 3L, 0, [exemplar]);
        MetricData[] shapes =
        [
            new GaugeData([number]), new SumData([number], 2, true),
            new HistogramData([new HistogramPoint(invalid, 0, 1, 1, 3, [1], [], [exemplar], 0, 3, 3)], 2),
            new ExponentialHistogramData([
                new ExponentialHistogramPoint(invalid, 0, 1, 1, 3, 0, 0,
                    null, null, 0, [exemplar], 3, 3, 0)
            ], 2),
            new SummaryData([new SummaryPoint(invalid, 0, 1, 1, 3, [], 0)])
        ];
        return new TelemetrySnapshot([], [], [.. shapes.Select(shape => Metric("custom.metric", "", shape))], []);
    }

    private static RegistryReport UnitsReport()
    {
        var span = new SpanRecord("trace", "span", "custom") { Attributes = Attributes(("db.system", "postgresql")) };
        MetricRecord[] metrics =
        [
            Metric("http.server.request.duration", "s", new SummaryData([])),
            Metric("http.server.request.duration", "ms", new GaugeData([])),
            Metric("container.memory.paging.faults", "{fault}", new SumData([], 2, true))
        ];
        return RegistryConformance.Check(new TelemetrySnapshot([span], [], metrics, []));
    }

    private static RegistryReport DeprecationReport()
    {
        var span = new SpanRecord("trace", "span", "custom") { Attributes = Attributes(("db.system", "postgresql")) };
        return RegistryConformance.Check(TestSpan.Only(span));
    }

    private static RegistryReport TemplateReport()
    {
        var span = new SpanRecord("trace", "span", "custom")
        {
            Attributes = Attributes(("http.request.header.content_type", new object?[] { "text/plain" }),
                ("http.request.headers.content_type", "custom"),
                ("pprof.scope.sample_type_order", new object?[] { 1L }),
                ("feature_flag.result.value", new Dictionary<string, object?>()))
        };
        return RegistryConformance.Check(TestSpan.Only(span));
    }

    private static RegistryReport ContextDependentReport(object threadId)
    {
        var span = new SpanRecord("11111111111111111111111111111111", "2222222222222222", "codex.exec")
        {
            Kind = SpanKind.Internal,
            Attributes = Attributes(("thread.id", threadId))
        };
        return RegistryConformance.Check(TestSpan.Only(span));
    }

    private static MetricRecord Metric(string name, string unit, MetricData data)
    {
        return new MetricRecord(new ResourceRecord(new Dictionary<string, object?>()), null, name, "", unit,
            new Dictionary<string, object?>(), data);
    }

    private static Dictionary<string, object?> Attributes(params (string Name, object? Value)[] values)
    {
        return values.ToDictionary(pair => pair.Name, pair => pair.Value, StringComparer.Ordinal);
    }
}
