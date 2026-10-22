using System.Diagnostics;
using Qyl.Telemetry.SemanticConventions;
using Qyl.Telemetry.SemanticConventions.Spans;

namespace Qyl.Telemetry.UnitTests.Receiver;

[TestClass]
public sealed class ConformanceTests
{
    [TestMethod]
    public void Conformance_EmptySelection_IsNotConformant()
    {
        Assert.IsFalse(EmptySelectionReport().IsConformant);
    }

    [TestMethod]
    public void Conformance_EmptySelection_ChecksNoSpan()
    {
        Assert.AreEqual(0, EmptySelectionReport().SpansChecked);
    }

    [TestMethod]
    public void Conformance_EmptySelection_ReportsThatNoSpanMatched()
    {
        Assert.AreEqual("No span matched the explicit conformance selection.",
            Assert.ContainsSingle(EmptySelectionReport().Issues).Message);
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
            new TraceEvidence(span.TraceId, TestSpan.Only(span)).Conformance(Definition(type),
                _ => true);
        Assert.AreEqual(expected, report.IsConformant);
        Assert.HasCount(expected ? 0 : 1, report.Issues);
    }

    [TestMethod]
    public void Conformance_DefinitionScopedReport_NamesTheDefinition()
    {
        Assert.AreEqual("test.server", StringFieldReport().DefinitionId);
    }

    [TestMethod]
    public void Conformance_DefinitionScopedReport_ChecksTheSelectedSpan()
    {
        Assert.AreEqual(1, StringFieldReport().SpansChecked);
    }

    [TestMethod]
    public void Conformance_UnconditionalDefinition_LeavesNoUnevaluatedCondition()
    {
        Assert.IsEmpty(StringFieldReport().UnevaluatedConditions);
    }

    [TestMethod]
    public void Conformance_MissingRequiredOrPresentDeprecated_IsNotConformant()
    {
        Assert.IsFalse(MixedRequirementReport().IsConformant);
    }

    [TestMethod]
    public void Conformance_MissingRequiredAndPresentDeprecated_AreReportedInDefinitionOrder()
    {
        Assert.AreSequenceEqual(["required", "deprecated"], MixedRequirementReport().Issues.Select(i => i.Attribute));
    }

    [TestMethod]
    public void Conformance_MissingRequiredAttribute_IsReportedAsAbsent()
    {
        Assert.AreEqual("Required attribute is absent.",
            Assert.ContainsSingle(MixedRequirementReport().Issues.Where(i => i.Attribute == "required")).Message);
    }

    [TestMethod]
    public void Conformance_PresentDeprecatedAttribute_IsReportedAsDeprecated()
    {
        Assert.AreEqual("The selected definition marks this attribute deprecated.",
            Assert.ContainsSingle(MixedRequirementReport().Issues.Where(i => i.Attribute == "deprecated")).Message);
    }

    [TestMethod]
    public void Conformance_AbsentConditionalAttribute_IsAnUnevaluatedCondition()
    {
        Assert.AreEqual("2222222222222222:conditional: When the operation retries.",
            Assert.ContainsSingle(MixedRequirementReport().UnevaluatedConditions));
    }

    [TestMethod]
    public void Conformance_WrongSpanKind_IsNotConformant()
    {
        Assert.IsFalse(ClientKindReport().IsConformant);
    }

    [TestMethod]
    public void Conformance_WrongSpanKind_IsReportedOnSpanKind()
    {
        Assert.AreEqual("span.kind", Assert.ContainsSingle(ClientKindReport().Issues).Attribute);
    }

    [TestMethod]
    public void Conformance_WrongSpanKind_NamesExpectedAndObservedKind()
    {
        Assert.AreEqual("Expected server, observed Client.", Assert.ContainsSingle(ClientKindReport().Issues).Message);
    }

    [TestMethod]
    [DataRow(RequirementLevel.Unspecified)]
    [DataRow(RequirementLevel.Recommended)]
    [DataRow(RequirementLevel.OptIn)]
    public void Conformance_AbsentNonRequiredAttribute_IsConformant(RequirementLevel level)
    {
        Assert.IsTrue(OptionalFieldReport(level).IsConformant);
    }

    [TestMethod]
    [DataRow(RequirementLevel.Unspecified)]
    [DataRow(RequirementLevel.Recommended)]
    [DataRow(RequirementLevel.OptIn)]
    public void Conformance_AbsentNonRequiredAttribute_ReportsNoIssue(RequirementLevel level)
    {
        Assert.IsEmpty(OptionalFieldReport(level).Issues);
    }

    [TestMethod]
    [DataRow(RequirementLevel.Unspecified)]
    [DataRow(RequirementLevel.Recommended)]
    [DataRow(RequirementLevel.OptIn)]
    public void Conformance_AbsentNonRequiredAttribute_LeavesNoUnevaluatedCondition(RequirementLevel level)
    {
        Assert.IsEmpty(OptionalFieldReport(level).UnevaluatedConditions);
    }

    [TestMethod]
    [DataRow(RequirementLevel.Unspecified)]
    [DataRow(RequirementLevel.Recommended)]
    [DataRow(RequirementLevel.OptIn)]
    public void Conformance_AbsentNonRequiredAttribute_StillChecksTheSpan(RequirementLevel level)
    {
        Assert.AreEqual(1, OptionalFieldReport(level).SpansChecked);
    }

    [TestMethod]
    [DataRow(RequirementLevel.Unspecified)]
    [DataRow(RequirementLevel.Recommended)]
    [DataRow(RequirementLevel.OptIn)]
    public void Conformance_AbsentNonRequiredAttribute_NamesTheDefinition(RequirementLevel level)
    {
        Assert.AreEqual("optional.server", OptionalFieldReport(level).DefinitionId);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Conformance_UndefinedPublicRequirementLevel_IsRejectedAsTheDefinitionArgument(bool present)
    {
        var error = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => CheckUndefinedLevel(present));
        Assert.AreEqual("definition", error.ParamName);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Conformance_UndefinedPublicRequirementLevel_IsRejectedWithItsValue(bool present)
    {
        var error = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => CheckUndefinedLevel(present));
        Assert.AreEqual((RequirementLevel)99, error.ActualValue);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Conformance_UndefinedPublicRequirementLevel_IsRejectedNamingTheAttribute(bool present)
    {
        var error = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => CheckUndefinedLevel(present));
        Assert.Contains("Attribute 'field' has an unsupported requirement level.", error.Message);
    }

    [TestMethod]
    public void Conformance_PresentConditionalAttribute_LeavesNoUnresolvedRequirement()
    {
        Assert.IsEmpty(PresentConditionalReport().UnevaluatedConditions);
    }

    [TestMethod]
    public void Conformance_PresentConditionalAttribute_IsConformant()
    {
        Assert.IsTrue(PresentConditionalReport().IsConformant);
    }

    [TestMethod]
    public void Conformance_PresentConditionalAttribute_ReportsNoIssue()
    {
        Assert.IsEmpty(PresentConditionalReport().Issues);
    }

    [TestMethod]
    public void Conformance_PresentConditionalAttribute_ChecksTheSpan()
    {
        Assert.AreEqual(1, PresentConditionalReport().SpansChecked);
    }

    [TestMethod]
    public void Conformance_GeneratedHttpServerDefinition_RetainsUnresolvedConditions()
    {
        Assert.AreSequenceEqual(
            [
                "error.type", "http.request.method_original", "http.response.status_code", "http.route",
                "network.protocol.name", "server.port", "url.query"
            ],
            HttpServerReport().UnevaluatedConditions.Select(condition => condition.Split(':')[1]));
    }

    [TestMethod]
    public void Conformance_GeneratedHttpServerWithUnresolvedConditions_IsNotConformant()
    {
        Assert.IsFalse(HttpServerReport().IsConformant);
    }

    [TestMethod]
    public void Conformance_GeneratedHttpServerWithRequiredAttributes_ReportsNoIssue()
    {
        Assert.IsEmpty(HttpServerReport().Issues);
    }

    [TestMethod]
    public void Conformance_GeneratedHttpServerDefinition_IsNamedHttpServer()
    {
        Assert.AreEqual("http.server", HttpServerReport().DefinitionId);
    }

    [TestMethod]
    public void Conformance_GeneratedHttpServerDefinition_ChecksTheSpan()
    {
        Assert.AreEqual(1, HttpServerReport().SpansChecked);
    }

    private static ConformanceReport Check(SpanRecord span, SpanDefinition<Server> definition) =>
        new TraceEvidence(span.TraceId, TestSpan.Only(span)).Conformance(definition, _ => true);

    private static ConformanceReport EmptySelectionReport() =>
        new TraceEvidence("trace", TestSpan.Only()).Conformance(Definition("string"), _ => true);

    private static ConformanceReport StringFieldReport() =>
        Check(Model(new Dictionary<string, object?> { ["field"] = Literal("string") }), Definition("string"));

    private static ConformanceReport MixedRequirementReport()
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
        return Check(Model(new Dictionary<string, object?> { ["deprecated"] = 7L }), definition);
    }

    private static ConformanceReport ClientKindReport() =>
        Check(new SpanRecord("11111111111111111111111111111111", "2222222222222222", "operation")
            { Kind = SpanKind.Client, Attributes = new Dictionary<string, object?> { ["field"] = "value" } }, Definition("string"));

    private static ConformanceReport OptionalFieldReport(RequirementLevel level) =>
        Check(Model(new Dictionary<string, object?>()), new SpanDefinition<Server>("optional.server", "optional.server",
            "Optional field contract", Stability.Stable, Deprecation.None,
            [new AttributeRef("optional", "string", level, false, null, AttributePhase.Start, true)], []));

    private static ConformanceReport CheckUndefinedLevel(bool present)
    {
        var attributes = new Dictionary<string, object?>();
        if (present) attributes["field"] = "value";
        return Check(Model(attributes), new SpanDefinition<Server>("invalid.server", "invalid.server",
            "Invalid requirement contract", Stability.Stable, Deprecation.None,
            [new AttributeRef("field", "string", (RequirementLevel)99, false, null, AttributePhase.Start, true)], []));
    }

    private static ConformanceReport PresentConditionalReport() =>
        Check(Model(new Dictionary<string, object?> { ["field"] = "value" }), new SpanDefinition<Server>("conditional.server",
            "conditional.server", "Conditional field contract", Stability.Stable, Deprecation.None,
            [
                new AttributeRef("field", "string", RequirementLevel.ConditionallyRequired, false, "When enabled",
                    AttributePhase.Start, true)
            ], []));

    private static ConformanceReport HttpServerReport() =>
        Check(Model(new Dictionary<string, object?>
        {
            ["http.request.method"] = "GET", ["url.path"] = "/checkout", ["url.scheme"] = "https"
        }), HttpSpanDefinitions.HttpServer);

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