using Qyl.Telemetry.SemanticConventions;

namespace Qyl.Telemetry.UnitTests.Receiver;

[TestClass]
public sealed class RegistryConformanceTests
{
    [TestMethod]
    public void RegistryAudit_NullCapture_ThrowsArgumentNullException()
    {
        _ = Assert.ThrowsExactly<ArgumentNullException>(() => RegistryConformance.Check(null!));
    }

    [TestMethod]
    public void RegistryAudit_EmptyCapture_IsNotConformant()
    {
        Assert.IsFalse(EmptyReport().IsConformant);
    }

    [TestMethod]
    public void RegistryAudit_EmptyCapture_ChecksNoRecord()
    {
        Assert.AreEqual(0, EmptyReport().RecordsChecked);
    }

    [TestMethod]
    public void RegistryAudit_EmptyCapture_ChecksNoAttribute()
    {
        Assert.AreEqual(0, EmptyReport().AttributesChecked);
    }

    [TestMethod]
    public void RegistryAudit_EmptyCapture_ChecksNoMetric()
    {
        Assert.AreEqual(0, EmptyReport().MetricsChecked);
    }

    [TestMethod]
    public void RegistryAudit_EmptyCapture_ReportsNoIssue()
    {
        Assert.IsEmpty(EmptyReport().Issues);
    }

    [TestMethod]
    public void RegistryAudit_CustomNamesAndOpenEnumValues_AreConformant()
    {
        Assert.IsTrue(CustomReport().IsConformant);
    }

    [TestMethod]
    public void RegistryAudit_CustomSpanAndMetric_AreBothCheckedAsRecords()
    {
        Assert.AreEqual(2, CustomReport().RecordsChecked);
    }

    [TestMethod]
    public void RegistryAudit_OpenEnumValue_IsTheOnlyCheckedAttribute()
    {
        Assert.AreEqual(1, CustomReport().AttributesChecked);
    }

    [TestMethod]
    public void RegistryAudit_CustomMetric_IsNotCheckedAgainstTheRegistry()
    {
        Assert.AreEqual(0, CustomReport().MetricsChecked);
    }

    [TestMethod]
    public void RegistryAudit_CustomAttribute_IsListedAsCustom()
    {
        Assert.AreSequenceEqual(["custom.payload"], CustomReport().CustomAttributes);
    }

    [TestMethod]
    public void RegistryAudit_CustomMetric_IsListedAsCustom()
    {
        Assert.AreSequenceEqual(["custom.metric"], CustomReport().CustomMetrics);
    }

    [TestMethod]
    public void RegistryAudit_InvalidAttributesOnEveryRecordPart_AreNotConformant()
    {
        Assert.IsFalse(RegistryConformance.Check(EveryRecordPartCapture()).IsConformant);
    }

    [TestMethod]
    public void RegistryAudit_LogBodyMap_IsNotCountedAsAnAttributeCollection()
    {
        Assert.AreEqual(8, RegistryConformance.Check(EveryRecordPartCapture()).AttributesChecked);
    }

    [TestMethod]
    public void RegistryAudit_SpanLogResourceScopeEventAndLinkAttributes_AreLocated()
    {
        Assert.AreSequenceEqual(
        [
            "spans[0].resource", "spans[0].scope", "spans[0]", "spans[0].events[0]", "spans[0].links[0]",
            "logs[0].resource", "logs[0].scope", "logs[0]"
        ], RegistryConformance.Check(EveryRecordPartCapture()).Issues.Select(i => i.Location));
    }

    [TestMethod]
    public void RegistryAudit_StringStatusCodeOnEveryRecordPart_IsAnIntegerTypeViolation()
    {
        Assert.IsTrue(RegistryConformance.Check(EveryRecordPartCapture()).Issues.All(i => i is
            { Name: "http.response.status_code", Message: "Expected Integer.", Level: RegistryIssueLevel.Violation }));
    }

    [TestMethod]
    public void RegistryAudit_LogBodyMap_IsLeftUnchanged()
    {
        var capture = EveryRecordPartCapture();
        var body = capture.Logs[0].Body;
        _ = RegistryConformance.Check(capture);
        Assert.AreSame(body, capture.Logs[0].Body);
    }

    [TestMethod]
    public void RegistryAudit_LogWithoutTimestamp_KeepsZeroTime()
    {
        var capture = EveryRecordPartCapture();
        _ = RegistryConformance.Check(capture);
        Assert.AreEqual(0UL, capture.Logs[0].TimeUnixNano);
    }

    [TestMethod]
    public void RegistryAudit_InvalidPointAttributes_AreNotConformant()
    {
        Assert.IsFalse(RegistryConformance.Check(EveryPointShapeCapture()).IsConformant);
    }

    [TestMethod]
    public void RegistryAudit_EveryPointShapeAndFilteredExemplar_AreCheckedAsAttributes()
    {
        Assert.AreEqual(9, RegistryConformance.Check(EveryPointShapeCapture()).AttributesChecked);
    }

    [TestMethod]
    public void RegistryAudit_EveryPointShapeAndFilteredExemplarAttributes_AreLocated()
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
    public void RegistryAudit_RegisteredMetrics_AreEachChecked()
    {
        Assert.AreEqual(3, UnitsReport().MetricsChecked);
    }

    [TestMethod]
    public void RegistryAudit_DeprecatedSpanAttribute_IsChecked()
    {
        Assert.AreEqual(1, UnitsReport().AttributesChecked);
    }

    [TestMethod]
    public void RegistryAudit_DeprecationsAndUnitMismatch_AreReportedWithoutInferringAnSdkInstrument()
    {
        Assert.AreSequenceEqual(["db.system", "http.server.request.duration", "container.memory.paging.faults"],
            UnitsReport().Issues.Select(i => i.Name));
    }

    [TestMethod]
    public void RegistryAudit_UnitMismatch_NamesExpectedAndObservedUnit()
    {
        Assert.AreEqual("Expected unit 's', observed 'ms'.", UnitsReport().Issues[1].Message);
    }

    [TestMethod]
    public void RegistryAudit_UnitMismatch_IsLocatedOnItsMetric()
    {
        Assert.AreEqual("metrics[1]", UnitsReport().Issues[1].Location);
    }

    [TestMethod]
    public void RegistryAudit_UnitMismatch_IsNotConformant()
    {
        Assert.IsFalse(UnitsReport().IsConformant);
    }

    [TestMethod]
    public void RegistryAudit_Deprecation_IsNotAProtocolFailure()
    {
        Assert.IsTrue(DeprecationReport().IsConformant);
    }

    [TestMethod]
    public void RegistryAudit_Deprecation_IsAnImprovement()
    {
        Assert.AreEqual(RegistryIssueLevel.Improvement, Assert.ContainsSingle(DeprecationReport().Issues).Level);
    }

    [TestMethod]
    public void RegistryAudit_Deprecation_NamesTheAttribute()
    {
        Assert.AreEqual("db.system", Assert.ContainsSingle(DeprecationReport().Issues).Name);
    }

    [TestMethod]
    public void RegistryAudit_Deprecation_IsLocatedOnItsSpan()
    {
        Assert.AreEqual("spans[0]", Assert.ContainsSingle(DeprecationReport().Issues).Location);
    }

    [TestMethod]
    public void RegistryAudit_Deprecation_SaysTheRegistryDeprecatesIt()
    {
        Assert.AreEqual("The pinned registry marks this attribute deprecated.", Assert.ContainsSingle(DeprecationReport().Issues).Message);
    }

    [TestMethod]
    public void RegistryAudit_TemplateAttributesWithDeclaredArrayKind_AreConformant()
    {
        Assert.IsTrue(TemplateReport().IsConformant);
    }

    [TestMethod]
    public void RegistryAudit_TemplateAttributes_AreCheckedButCustomNeighborIsNot()
    {
        Assert.AreEqual(3, TemplateReport().AttributesChecked);
    }

    [TestMethod]
    public void RegistryAudit_TemplateLookalikeNeighbor_IsListedAsCustom()
    {
        Assert.AreSequenceEqual(["http.request.headers.content_type"], TemplateReport().CustomAttributes);
    }

    [TestMethod]
    [DynamicData(nameof(ValueCases))]
    public void RegistryAudit_ValueKinds_PreserveOtlpTypes(RegistryAttributeValueKind kind, object? value, bool expected)
    {
        Assert.AreEqual(expected, RegistryConformance.Matches(kind, value));
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