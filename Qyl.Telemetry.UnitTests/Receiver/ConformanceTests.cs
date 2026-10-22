using System.Diagnostics;
using Qyl.Telemetry.SemanticConventions;
using Qyl.Telemetry.SemanticConventions.Spans;

namespace Qyl.Telemetry.UnitTests.Receiver;

[TestClass]
public sealed class ConformanceTests
{
    [TestMethod]
    public void Conformance_EmptySelection_FailsInsteadOfPassingAnEmptyLoop()
    {
        var trace = new TraceEvidence("trace", new TelemetrySnapshot([], [], [], []));
        var report = trace.Conformance(Definition("string"), _ => true);
        Assert.IsFalse(report.IsConformant);
        Assert.AreEqual(0, report.SpansChecked);
        Assert.AreEqual("No span matched the explicit conformance selection.",
            Assert.ContainsSingle(report.Issues).Message);
    }

    [TestMethod]
    [DataRow("string", "string", true)]
    [DataRow("string", "int", false)]
    [DataRow("int", "int", true)]
    [DataRow("double", "double", true)]
    [DataRow("boolean", "boolean", true)]
    [DataRow("string[]", "string[]", true)]
    [DataRow("int[]", "int[]", true)]
    [DataRow("double[]", "double[]", true)]
    [DataRow("boolean[]", "boolean[]", true)]
    [DataRow("enum", "string", true)]
    [DataRow("enum", "int", true)]
    [DataRow("string[]", "int[]", false)]
    [DataRow("int[]", "string[]", false)]
    [DataRow("double[]", "int[]", false)]
    [DataRow("boolean[]", "string[]", false)]
    [DataRow("string[]", "string", false)]
    [DataRow("int[]", "string", false)]
    [DataRow("double[]", "string", false)]
    [DataRow("boolean[]", "string", false)]
    [DataRow("int", "string", false)]
    [DataRow("double", "string", false)]
    [DataRow("boolean", "string", false)]
    [DataRow("enum", "boolean", false)]
    [DataRow("", "string", false)]
    [DataRow("str", "string", false)]
    [DataRow("u8", "string", false)]
    [DataRow("object[]", "string", false)]
    [DataRow("bytes", "string", false)]
    [DataRow("object", "string", false)]
    [DataRow("map", "string", false)]
    [DataRow("array", "string", false)]
    [DataRow("bool[]", "string", false)]
    [DataRow("float[]", "string", false)]
    [DataRow("integer[]", "string", false)]
    [DataRow("bool", "string", false)]
    [DataRow("float", "string", false)]
    [DataRow("integer", "string", false)]
    [DataRow("string?", "string", false)]
    [DataRow("array<string>", "string", false)]
    [DataRow("strinG", "string", false)]
    [DataRow("inT", "string", false)]
    [DataRow("doublE", "string", false)]
    [DataRow("booleaN", "string", false)]
    [DataRow("strinG[]", "string", false)]
    [DataRow("inT[]", "string", false)]
    [DataRow("doublE[]", "string", false)]
    [DataRow("booleaN[]", "string", false)]
    [DataRow("enuM", "string", false)]
    [DataRow("anY", "string", false)]
    [DataRow("templatE", "string", false)]
    [DataRow("unknown", "string", false)]
    [DataRow("any", "string", true)]
    [DataRow("template", "string", true)]
    public void Conformance_DefinitionScopedAttribute_IsCheckedAgainstItsDeclaredType(string type, string valueType,
        bool expected)
    {
        var span = Model(new Dictionary<string, object?> { ["field"] = Literal(valueType) });
        var report =
            new TraceEvidence(span.TraceId, new TelemetrySnapshot([span], [], [], [])).Conformance(Definition(type),
                _ => true);
        Assert.AreEqual("test.server", report.DefinitionId);
        Assert.AreEqual(1, report.SpansChecked);
        Assert.AreEqual(expected, report.IsConformant);
        Assert.HasCount(expected ? 0 : 1, report.Issues);
        Assert.IsEmpty(report.UnevaluatedConditions);
    }

    [TestMethod]
    public void Conformance_MissingRequiredDeprecatedOrUnknownConditions_AreReported()
    {
        var attributes = new AttributeRef[]
        {
            new("required", "string", RequirementLevel.Required, false, null, AttributePhase.Start, true),
            new("deprecated", "int", RequirementLevel.OptIn, true, null, AttributePhase.Start, false),
            new("conditional", "string", RequirementLevel.ConditionallyRequired, false, "When the operation retries.",
                AttributePhase.End, true)
        };
        var definition = new SpanDefinition<Server>("test.server", "test.server", "Explicit fixture contract",
            Stability.Stable, Deprecation.None, attributes, []);
        var span = Model(new Dictionary<string, object?> { ["deprecated"] = 7L });
        var report =
            new TraceEvidence(span.TraceId, new TelemetrySnapshot([span], [], [], [])).Conformance(definition, _ => true);
        Assert.IsFalse(report.IsConformant);
        Assert.AreSequenceEqual(["required", "deprecated"], report.Issues.Select(i => i.Attribute));
        Assert.AreEqual("Required attribute is absent.", report.Issues[0].Message);
        Assert.AreEqual("The selected definition marks this attribute deprecated.", report.Issues[1].Message);
        Assert.AreEqual("2222222222222222:conditional: When the operation retries.",
            Assert.ContainsSingle(report.UnevaluatedConditions));
    }

    [TestMethod]
    public void Conformance_WrongSpanKind_IsReportedAgainstTheSelectedDefinition()
    {
        var span = new SpanRecord("11111111111111111111111111111111", "2222222222222222", "operation")
            { Kind = SpanKind.Client, Attributes = new Dictionary<string, object?> { ["field"] = "value" } };
        var report =
            new TraceEvidence(span.TraceId, new TelemetrySnapshot([span], [], [], [])).Conformance(Definition("string"),
                _ => true);
        Assert.IsFalse(report.IsConformant);
        var issue = Assert.ContainsSingle(report.Issues);
        Assert.AreEqual("span.kind", issue.Attribute);
        Assert.AreEqual("Expected server, observed Client.", issue.Message);
    }

    [TestMethod]
    [DataRow(RequirementLevel.Unspecified)]
    [DataRow(RequirementLevel.Recommended)]
    [DataRow(RequirementLevel.OptIn)]
    public void Conformance_AbsentNonRequiredAttributes_Pass(RequirementLevel level)
    {
        var definition = new SpanDefinition<Server>("optional.server", "optional.server", "Optional field contract",
            Stability.Stable,
            Deprecation.None, [new AttributeRef("optional", "string", level, false, null, AttributePhase.Start, true)],
            []);
        var span = Model(new Dictionary<string, object?>());
        var report =
            new TraceEvidence(span.TraceId, new TelemetrySnapshot([span], [], [], [])).Conformance(definition, _ => true);
        Assert.AreEqual(1, report.SpansChecked);
        Assert.AreEqual("optional.server", report.DefinitionId);
        Assert.IsEmpty(report.Issues);
        Assert.IsEmpty(report.UnevaluatedConditions);
        Assert.IsTrue(report.IsConformant);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Conformance_UndefinedPublicRequirementLevel_IsRejected(bool present)
    {
        var attributes = new Dictionary<string, object?>();
        if (present) attributes["field"] = "value";
        var definition = new SpanDefinition<Server>("invalid.server", "invalid.server", "Invalid requirement contract",
            Stability.Stable,
            Deprecation.None,
            [new AttributeRef("field", "string", (RequirementLevel)99, false, null, AttributePhase.Start, true)], []);
        var span = Model(attributes);
        var error = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            new TraceEvidence(span.TraceId, new TelemetrySnapshot([span], [], [], [])).Conformance(definition, _ => true));
        Assert.AreEqual("definition", error.ParamName);
        Assert.AreEqual((RequirementLevel)99, error.ActualValue);
        Assert.Contains("Attribute 'field' has an unsupported requirement level.", error.Message);
    }

    [TestMethod]
    public void Conformance_PresentConditionalAttribute_LeavesNoUnresolvedRequirement()
    {
        var definition = new SpanDefinition<Server>("conditional.server", "conditional.server",
            "Conditional field contract", Stability.Stable,
            Deprecation.None,
            [
                new AttributeRef("field", "string", RequirementLevel.ConditionallyRequired, false, "When enabled",
                    AttributePhase.Start, true)
            ], []);
        var span = Model(new Dictionary<string, object?> { ["field"] = "value" });
        var report =
            new TraceEvidence(span.TraceId, new TelemetrySnapshot([span], [], [], [])).Conformance(definition, _ => true);
        Assert.AreEqual(1, report.SpansChecked);
        Assert.IsEmpty(report.Issues);
        Assert.IsEmpty(report.UnevaluatedConditions);
        Assert.IsTrue(report.IsConformant);
    }

    [TestMethod]
    public void Conformance_GeneratedHttpServerDefinition_RetainsUnresolvedConditions()
    {
        var span = Model(new Dictionary<string, object?>
        {
            ["http.request.method"] = "GET", ["url.path"] = "/checkout", ["url.scheme"] = "https"
        });
        var report = new TraceEvidence(span.TraceId, new TelemetrySnapshot([span], [], [], [])).Conformance(
            HttpSpanDefinitions.HttpServer, _ => true);
        Assert.AreEqual("http.server", report.DefinitionId);
        Assert.AreEqual(1, report.SpansChecked);
        Assert.IsEmpty(report.Issues);
        Assert.IsFalse(report.IsConformant);
        Assert.AreSequenceEqual(
            [
                "error.type", "http.request.method_original", "http.response.status_code", "http.route",
                "network.protocol.name", "server.port", "url.query"
            ],
            report.UnevaluatedConditions.Select(condition => condition.Split(':')[1]));
    }

    private static SpanDefinition<Server> Definition(string type)
    {
        return new SpanDefinition<Server>("test.server", "test.server", "Explicit fixture contract", Stability.Stable,
            Deprecation.None,
            [new AttributeRef("field", type, RequirementLevel.Required, false, null, AttributePhase.Start, true)], []);
    }

    private static SpanRecord Model(IReadOnlyDictionary<string, object?> attributes)
    {
        return new SpanRecord("11111111111111111111111111111111", "2222222222222222", "operation")
        {
            Kind = SpanKind.Server, Status = ActivityStatusCode.Ok,
            Scope = new InstrumentationScopeRecord { Name = "model-scope" },
            Attributes = attributes
        };
    }

    private static object Literal(string type)
    {
        return type switch
        {
            "string" => "value", "int" => 7L, "double" => 0.25, "boolean" => true,
            "string[]" => Array.AsReadOnly<object?>(["value"]),
            "int[]" => Array.AsReadOnly<object?>([7L]),
            "double[]" => Array.AsReadOnly<object?>([0.25]), "boolean[]" => Array.AsReadOnly<object?>([true]),
            _ => throw new ArgumentOutOfRangeException(nameof(type))
        };
    }
}