using Qyl.Telemetry.SemanticConventions;

namespace Qyl.Telemetry.UnitTests.Receiver;

[TestClass]
public sealed class RegistryConformanceTests
{
    [TestMethod]
    public void Empty_capture_is_not_evidence_and_null_capture_is_rejected()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => RegistryConformance.Check(null!));
        var report = RegistryConformance.Check(new TelemetrySnapshot([], [], [], []));
        Assert.IsFalse(report.IsConformant);
        Assert.AreEqual(0, report.RecordsChecked);
        Assert.AreEqual(0, report.AttributesChecked);
        Assert.AreEqual(0, report.MetricsChecked);
        Assert.IsEmpty(report.Issues);
    }

    [TestMethod]
    public void Custom_names_and_open_enum_values_are_valid_and_remain_visible()
    {
        var span = new SpanRecord("trace", "span", "custom")
        {
            Attributes = Attributes(("error.type", "application-specific"), ("custom.payload", new byte[] { 1, 2 }))
        };
        var metric = Metric("custom.metric", "widget", new UnspecifiedMetricData());
        var report = RegistryConformance.Check(new TelemetrySnapshot([span], [], [metric], []));
        Assert.IsTrue(report.IsConformant);
        Assert.AreEqual(2, report.RecordsChecked);
        Assert.AreEqual(1, report.AttributesChecked);
        Assert.AreEqual(0, report.MetricsChecked);
        Assert.AreSequenceEqual(["custom.payload"], report.CustomAttributes);
        Assert.AreSequenceEqual(["custom.metric"], report.CustomMetrics);
    }

    [TestMethod]
    public void Audit_reaches_span_log_resource_scope_event_and_link_attributes()
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
        var capture = new TelemetrySnapshot([span], [log], [], []);
        var report = RegistryConformance.Check(capture);
        Assert.IsFalse(report.IsConformant);
        Assert.AreEqual(8, report.AttributesChecked);
        Assert.AreSequenceEqual(
        [
            "spans[0].resource", "spans[0].scope", "spans[0]", "spans[0].events[0]", "spans[0].links[0]",
            "logs[0].resource", "logs[0].scope", "logs[0]"
        ], report.Issues.Select(i => i.Location));
        Assert.IsTrue(report.Issues.All(i => i is
            { Name: "http.response.status_code", Message: "Expected Integer.", Level: RegistryIssueLevel.Violation }));
        Assert.AreSame(invalid, capture.Logs[0].Body); // A log body map is not an attribute collection.
        Assert.AreEqual(0UL, capture.Logs[0].TimeUnixNano);
    }

    [TestMethod]
    public void Audit_reaches_every_point_shape_and_filtered_exemplar_attributes()
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
        var capture =
            new TelemetrySnapshot([], [], [.. shapes.Select(shape => Metric("custom.metric", "", shape))], []);
        var report = RegistryConformance.Check(capture);
        Assert.IsFalse(report.IsConformant);
        Assert.AreEqual(9, report.AttributesChecked);
        Assert.AreSequenceEqual(
            [
                "metrics[0].points[0]", "metrics[0].points[0].exemplars[0]", "metrics[1].points[0]",
                "metrics[1].points[0].exemplars[0]",
                "metrics[2].points[0]", "metrics[2].points[0].exemplars[0]", "metrics[3].points[0]",
                "metrics[3].points[0].exemplars[0]", "metrics[4].points[0]"
            ],
            report.Issues.Select(i => i.Location));
    }

    [TestMethod]
    public void Registered_units_and_deprecations_are_reported_without_inferring_an_sdk_instrument()
    {
        var span = new SpanRecord("trace", "span", "custom") { Attributes = Attributes(("db.system", "postgresql")) };
        MetricRecord[] metrics =
        [
            Metric("http.server.request.duration", "s", new SummaryData([])),
            Metric("http.server.request.duration", "ms", new GaugeData([])),
            Metric("container.memory.paging.faults", "{fault}", new SumData([], 2, true))
        ];
        var report = RegistryConformance.Check(new TelemetrySnapshot([span], [], metrics, []));
        Assert.AreEqual(3, report.MetricsChecked);
        Assert.AreEqual(1, report.AttributesChecked);
        Assert.AreSequenceEqual(["db.system", "http.server.request.duration", "container.memory.paging.faults"],
            report.Issues.Select(i => i.Name));
        Assert.AreEqual("Expected unit 's', observed 'ms'.", report.Issues[1].Message);
        Assert.AreEqual("metrics[1]", report.Issues[1].Location);
        Assert.IsFalse(report.IsConformant);
    }

    [TestMethod]
    public void Deprecation_is_visible_advice_without_a_false_protocol_failure()
    {
        var span = new SpanRecord("trace", "span", "custom") { Attributes = Attributes(("db.system", "postgresql")) };
        var report = RegistryConformance.Check(new TelemetrySnapshot([span], [], [], []));
        Assert.IsTrue(report.IsConformant);
        var issue = Assert.ContainsSingle(report.Issues);
        Assert.AreEqual(RegistryIssueLevel.Improvement, issue.Level);
        Assert.AreEqual("db.system", issue.Name);
        Assert.AreEqual("spans[0]", issue.Location);
        Assert.AreEqual("The pinned registry marks this attribute deprecated.", issue.Message);
    }

    [TestMethod]
    public void Template_attributes_use_their_declared_array_kind_without_rejecting_custom_neighbors()
    {
        var span = new SpanRecord("trace", "span", "custom")
        {
            Attributes = Attributes(("http.request.header.content_type", new object?[] { "text/plain" }),
                ("http.request.headers.content_type", "custom"),
                ("pprof.scope.sample_type_order", new object?[] { 1L }),
                ("feature_flag.result.value", new Dictionary<string, object?>()))
        };
        var report = RegistryConformance.Check(new TelemetrySnapshot([span], [], [], []));
        Assert.IsTrue(report.IsConformant);
        Assert.AreEqual(3, report.AttributesChecked);
        Assert.AreSequenceEqual(["http.request.headers.content_type"], report.CustomAttributes);
    }

    [TestMethod]
    [DynamicData(nameof(ValueCases))]
    public void Registry_value_kinds_preserve_otlp_types(RegistryAttributeValueKind kind, object? value, bool expected)
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