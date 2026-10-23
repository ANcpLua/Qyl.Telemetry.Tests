using System.Diagnostics;
using Qyl.Telemetry.SemanticConventions;
using Qyl.Telemetry.SemanticConventions.Spans;

namespace Qyl.Telemetry.UnitTests.Receiver;

[TestClass]
[TestCategory("Fast")]
public class ConformanceTests
{
    [TestMethod]
    public void Should_RejectAnIntegerHttpMethod_When_TheRegisteredEnumIsString()
    {
        var issue = Assert.ContainsSingle(CompleteHttpServerReport(7L).Issues);
        Assert.AreEqual("http.request.method", issue.Attribute);
    }

    [TestMethod]
    public void Should_AcceptAnOpenStringHttpMethod_When_TheRegisteredEnumIsString()
    {
        Assert.IsTrue(CompleteHttpServerReport("CUSTOM").IsConformant);
    }

    [TestMethod]
    public void Should_RejectAWrongHeaderType_When_TheTemplateExpansionIsPresent()
    {
        var issue = Assert.ContainsSingle(CompleteHttpServerReport("GET", "http.request.header.content_type", 42L).Issues);
        Assert.AreEqual("http.request.header.content_type", issue.Attribute);
    }

    [TestMethod]
    public void Should_AcceptAnEmptyStringArray_When_TheTemplateExpansionIsPresent()
    {
        Assert.IsTrue(CompleteHttpServerReport("GET", "http.request.header.content_type", Array.Empty<object?>()).IsConformant);
    }

    [TestMethod]
    public void Should_IgnoreACustomNeighbor_When_OnlyTheDeclaredTemplateIsSelected()
    {
        Assert.IsTrue(CompleteHttpServerReport("GET", "http.request.headers.content_type", 42L).IsConformant);
    }

    [TestMethod]
    [DataRow("field")]
    [DataRow("field.")]
    public void Should_ReportTheRequiredTemplateAbsent_When_NoNonemptyExpansionExists(string key)
    {
        var report = Check(Model(new Dictionary<string, object?> { [key] = "value" }), Definition("template"));
        Assert.AreEqual("Required attribute is absent.", Assert.ContainsSingle(report.Issues).Message);
    }

    [TestMethod]
    public void Should_BeNotConformant_When_Conformance_EmptySelection()
    {
        Assert.IsFalse(EmptySelectionReport().IsConformant);
    }

    [TestMethod]
    public void Should_CheckNoSpan_When_Conformance_EmptySelection()
    {
        Assert.AreEqual(0, EmptySelectionReport().SpansChecked);
    }

    [TestMethod]
    public void Should_ReportThatNoSpanMatched_When_Conformance_EmptySelection()
    {
        Assert.AreEqual("No span matched the explicit conformance selection.",
            Assert.ContainsSingle(EmptySelectionReport().Issues).Message);
    }

    [TestMethod]
    [DataRow("string", "string", true, 0)]
    [DataRow("string", "int", false, 1)]
    [DataRow("int", "int", true, 0)]
    [DataRow("double", "double", true, 0)]
    [DataRow("boolean", "boolean", true, 0)]
    [DataRow("string[]", "string[]", true, 0)]
    [DataRow("int[]", "int[]", true, 0)]
    [DataRow("double[]", "double[]", true, 0)]
    [DataRow("boolean[]", "boolean[]", true, 0)]
    [DataRow("enum", "string", true, 0)]
    [DataRow("enum", "int", true, 0)]
    [DataRow("string[]", "int[]", false, 1)]
    [DataRow("int[]", "string[]", false, 1)]
    [DataRow("double[]", "int[]", false, 1)]
    [DataRow("boolean[]", "string[]", false, 1)]
    [DataRow("string[]", "string", false, 1)]
    [DataRow("int[]", "string", false, 1)]
    [DataRow("double[]", "string", false, 1)]
    [DataRow("boolean[]", "string", false, 1)]
    [DataRow("int", "string", false, 1)]
    [DataRow("double", "string", false, 1)]
    [DataRow("boolean", "string", false, 1)]
    [DataRow("enum", "boolean", false, 1)]
    [DataRow("", "string", false, 1)]
    [DataRow("str", "string", false, 1)]
    [DataRow("u8", "string", false, 1)]
    [DataRow("object[]", "string", false, 1)]
    [DataRow("bytes", "string", false, 1)]
    [DataRow("object", "string", false, 1)]
    [DataRow("map", "string", false, 1)]
    [DataRow("array", "string", false, 1)]
    [DataRow("bool[]", "string", false, 1)]
    [DataRow("float[]", "string", false, 1)]
    [DataRow("integer[]", "string", false, 1)]
    [DataRow("bool", "string", false, 1)]
    [DataRow("float", "string", false, 1)]
    [DataRow("integer", "string", false, 1)]
    [DataRow("string?", "string", false, 1)]
    [DataRow("array<string>", "string", false, 1)]
    [DataRow("strinG", "string", false, 1)]
    [DataRow("inT", "string", false, 1)]
    [DataRow("doublE", "string", false, 1)]
    [DataRow("booleaN", "string", false, 1)]
    [DataRow("strinG[]", "string", false, 1)]
    [DataRow("inT[]", "string", false, 1)]
    [DataRow("doublE[]", "string", false, 1)]
    [DataRow("booleaN[]", "string", false, 1)]
    [DataRow("enuM", "string", false, 1)]
    [DataRow("anY", "string", false, 1)]
    [DataRow("templatE", "string", false, 1)]
    [DataRow("unknown", "string", false, 1)]
    [DataRow("any", "string", true, 0)]
    [DataRow("template", "string", true, 0)]
    public void Should_BeCheckedAgainstItsDeclaredType_When_Conformance_DefinitionScopedAttribute(string type, string valueType,
        bool expected, int expectedIssueCount)
    {
        var span = Model(new Dictionary<string, object?> { [type == "template" ? "field.entry" : "field"] = Literal(valueType) });
        var report =
            new TraceEvidence(span.TraceId, TestSpan.Only(span)).Conformance(Definition(type),
                _ => true);
        Assert.AreEqual(expected, report.IsConformant);
        Assert.HasCount(expectedIssueCount, report.Issues);
    }

    [TestMethod]
    public void Should_NameTheDefinition_When_Conformance_DefinitionScopedReport()
    {
        Assert.AreEqual("test.server", StringFieldReport().DefinitionId);
    }

    [TestMethod]
    public void Should_CheckTheSelectedSpan_When_Conformance_DefinitionScopedReport()
    {
        Assert.AreEqual(1, StringFieldReport().SpansChecked);
    }

    [TestMethod]
    public void Should_LeaveNoUnevaluatedCondition_When_Conformance_UnconditionalDefinition()
    {
        Assert.IsEmpty(StringFieldReport().UnevaluatedConditions);
    }

    [TestMethod]
    public void Should_BeNotConformant_When_Conformance_MissingRequiredOrPresentDeprecated()
    {
        Assert.IsFalse(MixedRequirementReport().IsConformant);
    }

    [TestMethod]
    public void Should_BeReportedInDefinitionOrder_When_Conformance_MissingRequiredAndPresentDeprecated()
    {
        Assert.AreSequenceEqual(["required", "deprecated"], MixedRequirementReport().Issues.Select(i => i.Attribute));
    }

    [TestMethod]
    public void Should_BeReportedAsAbsent_When_Conformance_MissingRequiredAttribute()
    {
        Assert.AreEqual("Required attribute is absent.",
            Assert.ContainsSingle(MixedRequirementReport().Issues.Where(i => i.Attribute == "required")).Message);
    }

    [TestMethod]
    public void Should_BeReportedAsDeprecated_When_Conformance_PresentDeprecatedAttribute()
    {
        Assert.AreEqual("The selected definition marks this attribute deprecated.",
            Assert.ContainsSingle(MixedRequirementReport().Issues.Where(i => i.Attribute == "deprecated")).Message);
    }

    [TestMethod]
    public void Should_BeAnUnevaluatedCondition_When_Conformance_AbsentConditionalAttribute()
    {
        Assert.AreEqual("2222222222222222:conditional: When the operation retries.",
            Assert.ContainsSingle(MixedRequirementReport().UnevaluatedConditions));
    }

    [TestMethod]
    public void Should_BeNotConformant_When_Conformance_WrongSpanKind()
    {
        Assert.IsFalse(ClientKindReport().IsConformant);
    }

    [TestMethod]
    public void Should_BeReportedOnSpanKind_When_Conformance_WrongSpanKind()
    {
        Assert.AreEqual("span.kind", Assert.ContainsSingle(ClientKindReport().Issues).Attribute);
    }

    [TestMethod]
    public void Should_NameExpectedAndObservedKind_When_Conformance_WrongSpanKind()
    {
        Assert.AreEqual("Expected server, observed Client.", Assert.ContainsSingle(ClientKindReport().Issues).Message);
    }

    [TestMethod]
    [DataRow(RequirementLevel.Unspecified)]
    [DataRow(RequirementLevel.Recommended)]
    [DataRow(RequirementLevel.OptIn)]
    public void Should_BeConformant_When_Conformance_AbsentNonRequiredAttribute(RequirementLevel level)
    {
        Assert.IsTrue(OptionalFieldReport(level).IsConformant);
    }

    [TestMethod]
    [DataRow(RequirementLevel.Unspecified)]
    [DataRow(RequirementLevel.Recommended)]
    [DataRow(RequirementLevel.OptIn)]
    public void Should_ReportNoIssue_When_Conformance_AbsentNonRequiredAttribute(RequirementLevel level)
    {
        Assert.IsEmpty(OptionalFieldReport(level).Issues);
    }

    [TestMethod]
    [DataRow(RequirementLevel.Unspecified)]
    [DataRow(RequirementLevel.Recommended)]
    [DataRow(RequirementLevel.OptIn)]
    public void Should_LeaveNoUnevaluatedCondition_When_Conformance_AbsentNonRequiredAttribute(RequirementLevel level)
    {
        Assert.IsEmpty(OptionalFieldReport(level).UnevaluatedConditions);
    }

    [TestMethod]
    [DataRow(RequirementLevel.Unspecified)]
    [DataRow(RequirementLevel.Recommended)]
    [DataRow(RequirementLevel.OptIn)]
    public void Should_StillCheckTheSpan_When_Conformance_AbsentNonRequiredAttribute(RequirementLevel level)
    {
        Assert.AreEqual(1, OptionalFieldReport(level).SpansChecked);
    }

    [TestMethod]
    [DataRow(RequirementLevel.Unspecified)]
    [DataRow(RequirementLevel.Recommended)]
    [DataRow(RequirementLevel.OptIn)]
    public void Should_NameTheDefinition_When_Conformance_AbsentNonRequiredAttribute(RequirementLevel level)
    {
        Assert.AreEqual("optional.server", OptionalFieldReport(level).DefinitionId);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Should_BeRejectedAsTheDefinitionArgument_When_Conformance_UndefinedPublicRequirementLevel(bool present)
    {
        var error = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => CheckUndefinedLevel(present));
        Assert.AreEqual("definition", error.ParamName);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Should_BeRejectedWithItsValue_When_Conformance_UndefinedPublicRequirementLevel(bool present)
    {
        var error = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => CheckUndefinedLevel(present));
        Assert.AreEqual((RequirementLevel)99, error.ActualValue);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Should_BeRejectedNamingTheAttribute_When_Conformance_UndefinedPublicRequirementLevel(bool present)
    {
        var error = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => CheckUndefinedLevel(present));
        Assert.Contains("Attribute 'field' has an unsupported requirement level.", error.Message);
    }

    [TestMethod]
    public void Should_LeaveNoUnresolvedRequirement_When_Conformance_PresentConditionalAttribute()
    {
        Assert.IsEmpty(PresentConditionalReport().UnevaluatedConditions);
    }

    [TestMethod]
    public void Should_BeConformant_When_Conformance_PresentConditionalAttribute()
    {
        Assert.IsTrue(PresentConditionalReport().IsConformant);
    }

    [TestMethod]
    public void Should_ReportNoIssue_When_Conformance_PresentConditionalAttribute()
    {
        Assert.IsEmpty(PresentConditionalReport().Issues);
    }

    [TestMethod]
    public void Should_CheckTheSpan_When_Conformance_PresentConditionalAttribute()
    {
        Assert.AreEqual(1, PresentConditionalReport().SpansChecked);
    }

    [TestMethod]
    public void Should_RetainUnresolvedConditions_When_Conformance_GeneratedHttpServerDefinition()
    {
        Assert.AreSequenceEqual(
            [
                "error.type", "http.request.method_original", "http.response.status_code", "http.route",
                "network.protocol.name", "server.port", "url.query"
            ],
            HttpServerReport().UnevaluatedConditions.Select(condition => condition.Split(':')[1]));
    }

    [TestMethod]
    public void Should_BeNotConformant_When_Conformance_GeneratedHttpServerWithUnresolvedConditions()
    {
        Assert.IsFalse(HttpServerReport().IsConformant);
    }

    [TestMethod]
    public void Should_ReportNoIssue_When_Conformance_GeneratedHttpServerWithRequiredAttributes()
    {
        Assert.IsEmpty(HttpServerReport().Issues);
    }

    [TestMethod]
    public void Should_BeNamedHttpServer_When_Conformance_GeneratedHttpServerDefinition()
    {
        Assert.AreEqual("http.server", HttpServerReport().DefinitionId);
    }

    [TestMethod]
    public void Should_CheckTheSpan_When_Conformance_GeneratedHttpServerDefinition()
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

    private static ConformanceReport CompleteHttpServerReport(object method, string? extraKey = null, object? extraValue = null)
    {
        var attributes = new Dictionary<string, object?>
        {
            ["http.request.method"] = method, ["url.path"] = "/orders", ["url.scheme"] = "https",
            ["error.type"] = "example.Error", ["http.request.method_original"] = "GET",
            ["http.response.status_code"] = 500L, ["http.route"] = "/orders",
            ["network.protocol.name"] = "http", ["server.port"] = 443L, ["url.query"] = ""
        };
        if (extraKey is not null) attributes[extraKey] = extraValue;
        return Check(Model(attributes), HttpSpanDefinitions.HttpServer);
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
