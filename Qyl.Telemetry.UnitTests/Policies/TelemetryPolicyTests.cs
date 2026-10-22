using System.Diagnostics;
using Qyl.Api.Contracts.Common;
using Qyl.Api.Contracts.OTel.Traces;
using Qyl.Telemetry.SemanticConventions;
using ContractAttribute = Qyl.Api.Contracts.Common.Attribute;
using ContractKind = Qyl.Api.Contracts.OTel.Enums.SpanKind;
using ContractStatus = Qyl.Api.Contracts.OTel.Enums.SpanStatusCode;
using SpanKind = Qyl.Telemetry.SemanticConventions.SpanKind;
using Resource = Qyl.Api.Contracts.OTel.Resource.Resource;
using StoredSpan = Qyl.Api.Contracts.OTel.Traces.Span;

namespace Qyl.Telemetry.UnitTests.Policies;

/// <summary>Each policy decides from explicit published evidence, names the violating span and never passes empty.</summary>
[TestClass]
public sealed class TelemetryPolicyTests
{
    private const string Trace = "00112233445566778899aabbccddeeff";
    private const string Root = "0000000000000001";
    private const string Server = "0000000000000002";
    private const string Client = "0000000000000003";
    private const string Missing = "00000000000000ff";
    private const string Selected = "http.request.header.x-request-id";
    private const string DeprecatedGrpcMetadata = "rpc.grpc.request.metadata.authorization";

    [TestMethod]
    [DynamicData(nameof(EveryPolicy))]
    public void TelemetryPolicy_NoTracePublished_FailsThePolicy(TelemetryPolicyAttribute policy)
    {
        using var invocation = TelemetryInvocation.Begin([policy]);
        Assert.AreSequenceEqual([$"[{policy.Name}] No trace was published. Capture through "
            + "TelemetryCapture.TraceAsync or a TelemetryPipeline root inside the test."], invocation.Violations());
    }

    public static IEnumerable<object[]> EveryPolicy()
    {
        yield return [new ConnectedTraceAttribute()];
        yield return [new NoDuplicateSpansAttribute()];
        yield return [new CapturesExactlyAttribute()];
        yield return [new ConformsToSemanticConventionsAttribute()];
        yield return [new PreservedByCollectorAttribute()];
        yield return [new NoErrorTelemetryAttribute()];
        yield return [new ExportsCleanlyAttribute()];
    }

    [TestMethod]
    public void ConnectedTrace_SpansUnderCapturedParentsOrTheTestRoot_Pass()
    {
        Assert.IsEmpty(Violations(new ConnectedTraceAttribute(), Span(Server, Root), Span(Client, Server, SpanKind.Client)));
    }

    [TestMethod]
    public void ConnectedTrace_LostParent_IsNamedInTheFailure()
    {
        Assert.AreSequenceEqual(
        [
            "[ConnectedTrace] 'GET /orders' (Server, orders, Microsoft.AspNetCore) names parent 00000000000000ff, "
            + "which is neither a captured span nor the test root."
        ], Violations(new ConnectedTraceAttribute(), Span(Server, Missing)));
    }

    [TestMethod]
    public void ConnectedTrace_SecondRoot_IsNamedInTheFailure()
    {
        Assert.AreSequenceEqual(
        [
            "[ConnectedTrace] 'GET /orders' (Client, orders, Microsoft.AspNetCore) starts a second root in trace "
            + "00112233445566778899aabbccddeeff."
        ], Violations(new ConnectedTraceAttribute(), Span(Server, Root), Span(Client, "", SpanKind.Client)));
    }

    [TestMethod]
    public void ConnectedTrace_EmptyTrace_IsNamedInTheFailure()
    {
        var empty = new PublishedTrace(new TraceEvidence(Trace, new TelemetrySnapshot([], [], [], [])), Root);
        Assert.AreSequenceEqual(["[ConnectedTrace] Trace 00112233445566778899aabbccddeeff exported no span."],
            Violations(new ConnectedTraceAttribute(), empty));
    }

    [TestMethod]
    public void NoDuplicateSpans_RepeatedExport_IsNamedInTheFailure()
    {
        Assert.AreSequenceEqual(
            ["[NoDuplicateSpans] 'GET /orders' (Server, orders, Microsoft.AspNetCore) was exported 2 times as span 0000000000000002."],
            Violations(new NoDuplicateSpansAttribute(), Span(Server, Root), Span(Server, Root)));
    }

    [TestMethod]
    public void NoDuplicateSpans_WrappedOperation_IsNamedInTheFailure()
    {
        Assert.AreSequenceEqual(
        [
            "[NoDuplicateSpans] 'GET /orders' (Server, orders, Contrib.AspNetCore) repeats its parent "
            + "'GET /orders' (Server, orders, Microsoft.AspNetCore): the operation is instrumented twice."
        ], Violations(new NoDuplicateSpansAttribute(), Span(Server, Root), Span(Client, Server, scope: "Contrib.AspNetCore")));
    }

    [TestMethod]
    public void NoDuplicateSpans_ConcurrentSecondScope_IsNamedInTheFailure()
    {
        Assert.AreSequenceEqual(
        [
            "[NoDuplicateSpans] 'GET /orders' (Server, orders, Microsoft.AspNetCore) and 'GET /orders' (Server, orders, "
            + "Contrib.AspNetCore) describe the same operation concurrently: the operation is instrumented twice."
        ], Violations(new NoDuplicateSpansAttribute(), Span(Server, Root), Span("0000000000000004", Root, scope: "Contrib.AspNetCore")));
    }

    [TestMethod]
    public void NoDuplicateSpans_LayeredGrpcOverHttp_Passes()
    {
        var grpc = Span(Client, Root, SpanKind.Client, "orders.Orders/Create");
        var http = Span("0000000000000004", Client, SpanKind.Client, "POST");
        Assert.IsEmpty(Violations(new NoDuplicateSpansAttribute(), grpc, http));
    }

    [TestMethod]
    public void NoDuplicateSpans_ParallelCallsOfOneScope_Pass()
    {
        var first = Span("0000000000000005", Root, SpanKind.Client);
        var second = Span("0000000000000006", Root, SpanKind.Client);
        Assert.IsEmpty(Violations(new NoDuplicateSpansAttribute(), first, second));
    }

    [TestMethod]
    public void CapturesExactly_NonHeaderField_IsReportedAsInvalid()
    {
        Assert.AreSequenceEqual(
            ["[CapturesExactly] 'url.path' is not an HTTP header or RPC metadata attribute."],
            Violations(new CapturesExactlyAttribute("url.path"), Span(Server, Root)));
    }

    [TestMethod]
    public void CapturesExactly_SelectedFieldNeverCaptured_IsReportedAsLost()
    {
        Assert.AreSequenceEqual(
            ["[CapturesExactly] 'http.request.header.x-request-id' was selected, but no span captured it."],
            Violations(new CapturesExactlyAttribute(Selected), Span(Server, Root)));
    }

    [TestMethod]
    public void CapturesExactly_UnselectedCapturedHeader_IsReported()
    {
        var server = Span(Server, Root, attributes: new Dictionary<string, object?>
        {
            ["http.request.header.authorization"] = new object?[] { "secret" }
        });
        Assert.AreSequenceEqual(
        [
            "[CapturesExactly] 'GET /orders' (Server, orders, Microsoft.AspNetCore) captured unselected "
            + "'http.request.header.authorization'."
        ], Violations(new CapturesExactlyAttribute(), server));
    }

    [TestMethod]
    public void CapturesExactly_UnselectedDeprecatedGrpcMetadata_IsReported()
    {
        var server = Span(Server, Root, attributes: new Dictionary<string, object?>
        {
            [DeprecatedGrpcMetadata] = new object?[] { "secret" }
        });
        Assert.AreSequenceEqual(
        [
            "[CapturesExactly] 'GET /orders' (Server, orders, Microsoft.AspNetCore) captured unselected "
            + "'rpc.grpc.request.metadata.authorization'."
        ], Violations(new CapturesExactlyAttribute(), server));
    }

    [TestMethod]
    public void CapturesExactly_ExactSelection_Passes()
    {
        var selected = Span(Server, Root, attributes: new Dictionary<string, object?> { [Selected] = new object?[] { "request-1" } });
        Assert.IsEmpty(Violations(new CapturesExactlyAttribute(Selected), selected));
    }

    [TestMethod]
    public void CapturesExactly_EmptySelectionWithoutFields_Passes()
    {
        Assert.IsEmpty(Violations(new CapturesExactlyAttribute(), Span(Server, Root)));
    }

    [TestMethod]
    public void ConformsToSemanticConventions_StringStatusCode_IsLocatedAsTypeViolation()
    {
        Assert.AreSequenceEqual(
        [
            "[ConformsToSemanticConventions] spans[0] 'GET /orders' (Server, orders, Microsoft.AspNetCore) "
            + "'http.response.status_code': Expected Integer."
        ], Violations(new ConformsToSemanticConventionsAttribute(), StatusCodeAsString()));
    }

    [TestMethod]
    public void ConformsToSemanticConventions_DeprecatedAttribute_IsLocated()
    {
        Assert.AreSequenceEqual(
        [
            "[ConformsToSemanticConventions] spans[0] 'GET /orders' (Server, orders, Microsoft.AspNetCore) "
            + "'db.system': The pinned registry marks this attribute deprecated."
        ], Violations(new ConformsToSemanticConventionsAttribute(), DeprecatedDbSystem()));
    }

    [TestMethod]
    public void ConformsToSemanticConventions_DeprecatedAttributeWithAllowDeprecated_Passes()
    {
        Assert.IsEmpty(Violations(new ConformsToSemanticConventionsAttribute { AllowDeprecated = true }, DeprecatedDbSystem()));
    }

    [TestMethod]
    public void ConformsToSemanticConventions_TypeViolationWithAllowDeprecated_IsStillLocated()
    {
        Assert.AreSequenceEqual(
        [
            "[ConformsToSemanticConventions] spans[0] 'GET /orders' (Server, orders, Microsoft.AspNetCore) "
            + "'http.response.status_code': Expected Integer."
        ], Violations(new ConformsToSemanticConventionsAttribute { AllowDeprecated = true }, StatusCodeAsString()));
    }

    [TestMethod]
    public void ConformsToSemanticConventions_CustomAttribute_Passes()
    {
        var server = Span(Server, Root, attributes: new Dictionary<string, object?> { ["orders.custom"] = 7L });
        Assert.IsEmpty(Violations(new ConformsToSemanticConventionsAttribute(), server));
    }

    [TestMethod]
    public void NoErrorTelemetry_ErrorStatusOnTraceSpan_IsReported()
    {
        Assert.AreSequenceEqual(
            ["[NoErrorTelemetry] 'GET /orders' (Server, orders, Microsoft.AspNetCore) ended with error status: boom"],
            Violations(new NoErrorTelemetryAttribute(), Published(Failed(withException: false))));
    }

    [TestMethod]
    public void NoErrorTelemetry_ExceptionEventOnTraceSpan_IsReported()
    {
        Assert.Contains("[NoErrorTelemetry] 'GET /orders' (Server, orders, Microsoft.AspNetCore) recorded "
            + "\"System.InvalidOperationException\": \"boom\"",
            Violations(new NoErrorTelemetryAttribute(), Published(Failed(withException: true))));
    }

    [TestMethod]
    public void NoErrorTelemetry_ErrorLogWithoutTrace_IsReported()
    {
        var capture = new TelemetrySnapshot([Span(Server, Root)], [Log(17, "Microsoft.AspNetCore.Server.Kestrel", null)], [], []);
        Assert.AreSequenceEqual(
            ["[NoErrorTelemetry] orders logged Error from Microsoft.AspNetCore.Server.Kestrel: ConnectionError"],
            Violations(new NoErrorTelemetryAttribute(), new PublishedTrace(new TraceEvidence(Trace, capture), Root)));
    }

    [TestMethod]
    public void NoErrorTelemetry_InformationLogOfTheTrace_Passes()
    {
        var capture = new TelemetrySnapshot([Span(Server, Root)], [Log(13, "Orders", Trace)], [], []);
        Assert.IsEmpty(Violations(new NoErrorTelemetryAttribute(), new PublishedTrace(new TraceEvidence(Trace, capture), Root)));
    }

    [TestMethod]
    public void NoErrorTelemetry_ErrorFixtureSpanOfAnotherTrace_Passes()
    {
        var healthProbe = new SpanRecord("ffeeddccbbaa99887766554433221100", Client, "GET")
        {
            Kind = SpanKind.Client,
            Status = ActivityStatusCode.Error
        };
        var capture = new TelemetrySnapshot([Span(Server, Root), healthProbe], [], [], []);
        Assert.IsEmpty(Violations(new NoErrorTelemetryAttribute(), new PublishedTrace(new TraceEvidence(Trace, capture), Root)));
    }

    [TestMethod]
    public void NoErrorTelemetry_HealthySpan_Passes()
    {
        Assert.IsEmpty(Violations(new NoErrorTelemetryAttribute(), Span(Server, Root)));
    }

    [TestMethod]
    public void ExportsCleanly_ReceiverWarning_IsReported()
    {
        Assert.AreSequenceEqual(["[ExportsCleanly] A HttpProtobuf logs export: Coalesced identical OTLP attribute: ConnectionId."],
            Violations(new ExportsCleanlyAttribute(), WithReceipts(new ExportReceipt("logs", 1, 0, null) { Warnings = ["Coalesced identical OTLP attribute: ConnectionId."] })));
    }

    [TestMethod]
    public void ExportsCleanly_RejectedRecords_AreReportedWithTheError()
    {
        Assert.AreSequenceEqual(["[ExportsCleanly] A Grpc traces export rejected 2 record(s): invalid span id"],
            Violations(new ExportsCleanlyAttribute(), WithReceipts(new ExportReceipt("traces", 1, 2, "invalid span id") { Transport = OtlpTransport.Grpc })));
    }

    [TestMethod]
    public void ExportsCleanly_CleanExport_Passes()
    {
        Assert.IsEmpty(Violations(new ExportsCleanlyAttribute(), WithReceipts(new ExportReceipt("metrics", 3, 0, null))));
    }

    [TestMethod]
    public void ExportsCleanly_NoExportRecorded_Fails()
    {
        Assert.AreSequenceEqual(["[ExportsCleanly] The capture recorded no export."],
            Violations(new ExportsCleanlyAttribute(), Span(Server, Root)));
    }

    [TestMethod]
    public void PreservedByCollector_IdenticalStorageWithPolicyDeniedKeysWithheld_Passes()
    {
        var captured = Span(Server, Root, attributes: new Dictionary<string, object?>
        {
            ["http.response.status_code"] = 200L,
            [Selected] = new object?[] { "a", "b" },
            ["session.id"] = "lifted into a column"
        });
        var stored = Stored(captured, captured.Name, Attribute("http.response.status_code", new AttributeValue.ObjectValue(new AttributeIntValue { Value = 200 })),
            Attribute(Selected, new AttributeValue.ArrayValue([new AttributeValue.StringValue("a"), new AttributeValue.StringValue("b")])),
            Attribute("service.name", new AttributeValue.StringValue("orders")));
        Assert.IsEmpty(Violations(new PreservedByCollectorAttribute(), Published(captured) with { Stored = [stored] }));
    }

    [TestMethod]
    public void PreservedByCollector_RenamedSpan_IsNamedInTheFailure()
    {
        var captured = StatusSpan();
        Assert.AreSequenceEqual(["[PreservedByCollector] 'GET /orders' (Server, orders, Microsoft.AspNetCore) was stored as 'GET'."],
            Violations(new PreservedByCollectorAttribute(), Published(captured) with { Stored = [Stored(captured, "GET", IntegerStatus)] }));
    }

    [TestMethod]
    public void PreservedByCollector_ChangedValueType_IsNamedInTheFailure()
    {
        var captured = StatusSpan();
        Assert.AreSequenceEqual(
        [
            "[PreservedByCollector] 'GET /orders' (Server, orders, Microsoft.AspNetCore) changed "
            + "'http.response.status_code' from 200 to \"200\"."
        ], Violations(new PreservedByCollectorAttribute(), Published(captured) with
        {
            Stored = [Stored(captured, captured.Name, Attribute("http.response.status_code", new AttributeValue.StringValue("200")))]
        }));
    }

    [TestMethod]
    public void PreservedByCollector_RetainedPolicyDeniedKey_IsNamedInTheFailure()
    {
        var captured = Span(Server, Root, attributes: new Dictionary<string, object?> { ["session.id"] = "lifted into a column" });
        Assert.AreSequenceEqual(
        [
            "[PreservedByCollector] 'GET /orders' (Server, orders, Microsoft.AspNetCore) retained 'session.id', "
            + "which the persistence policy denies."
        ], Violations(new PreservedByCollectorAttribute(), Published(captured) with
        {
            Stored = [Stored(captured, captured.Name, Attribute("session.id", new AttributeValue.StringValue("lifted into a column")))]
        }));
    }

    [TestMethod]
    public void PreservedByCollector_LostAttribute_IsNamedInTheFailure()
    {
        var captured = Span(Server, Root, attributes: new Dictionary<string, object?> { ["url.path"] = "/orders" });
        Assert.AreSequenceEqual(["[PreservedByCollector] 'GET /orders' (Server, orders, Microsoft.AspNetCore) lost 'url.path' = \"/orders\"."],
            Violations(new PreservedByCollectorAttribute(), Published(captured) with { Stored = [Stored(captured, captured.Name)] }));
    }

    [TestMethod]
    public void PreservedByCollector_GainedAttribute_IsNamedInTheFailure()
    {
        var captured = Span(Server, Root);
        Assert.AreSequenceEqual(["[PreservedByCollector] 'GET /orders' (Server, orders, Microsoft.AspNetCore) gained 'qyl.added' = true."],
            Violations(new PreservedByCollectorAttribute(), Published(captured) with
            {
                Stored = [Stored(captured, captured.Name, Attribute("qyl.added", new AttributeValue.BoolValue(true)))]
            }));
    }

    [TestMethod]
    public void PreservedByCollector_LostResourceAttribute_IsNamedInTheFailure()
    {
        var captured = Span(Server, Root);
        Assert.AreSequenceEqual(["[PreservedByCollector] 'GET /orders' (Server, orders, Microsoft.AspNetCore) resource lost 'service.name' = \"orders\"."],
            Violations(new PreservedByCollectorAttribute(), Published(captured) with { Stored = [Stored(captured, captured.Name, resource: [])] }));
    }

    [TestMethod]
    public void PreservedByCollector_CapturedSpanNotStored_IsNamedInTheFailure()
    {
        var captured = Span(Server, Root);
        var lost = Span(Client, Server, SpanKind.Client);
        Assert.AreSequenceEqual(
        [
            "[PreservedByCollector] 'GET /orders' (Client, orders, Microsoft.AspNetCore) span 0000000000000003 was "
            + "captured but not stored."
        ], Violations(new PreservedByCollectorAttribute(), Published(captured, lost) with { Stored = [Stored(captured, captured.Name)] }));
    }

    [TestMethod]
    public void PreservedByCollector_TraceNotReadBack_Fails()
    {
        Assert.AreSequenceEqual(
        [
            "[PreservedByCollector] Trace 00112233445566778899aabbccddeeff was not read back from a Collector. "
            + "Capture it through a TelemetryPipeline root; TelemetryCapture.TraceAsync has no Collector."
        ], Violations(new PreservedByCollectorAttribute(), Span(Server, Root)));
    }

    [TestMethod]
    public void TelemetryPolicy_Violation_FailsThePassingResult()
    {
        var (passed, _, _) = VerifyLostParent();
        Assert.AreEqual(UnitTestOutcome.Failed, passed.Outcome);
    }

    [TestMethod]
    public void TelemetryPolicy_Violation_StartsTheFailureWithTheViolation()
    {
        var (passed, _, _) = VerifyLostParent();
        var failure = Assert.IsInstanceOfType<AssertFailedException>(passed.TestFailureException);
        Assert.StartsWith("Telemetry policy violated (1):" + Environment.NewLine + "- [ConnectedTrace] 'GET /orders'", failure.Message);
    }

    [TestMethod]
    public void TelemetryPolicy_Violation_OutlinesTheTraceInTheFailure()
    {
        var (passed, _, _) = VerifyLostParent();
        var failure = Assert.IsInstanceOfType<AssertFailedException>(passed.TestFailureException);
        Assert.Contains("GET /orders  Server  orders  Microsoft.AspNetCore  span 0000000000000002  "
            + "(parent 00000000000000ff was not captured)", failure.Message);
    }

    [TestMethod]
    public void TelemetryPolicy_Violation_AttachesTheEvidenceTheFailureCites()
    {
        var (passed, _, _) = VerifyLostParent();
        var failure = Assert.IsInstanceOfType<AssertFailedException>(passed.TestFailureException);
        var evidence = Assert.ContainsSingle(passed.ResultFiles!);
        Assert.EndsWith("Evidence: " + evidence, failure.Message);
    }

    [TestMethod]
    public void TelemetryPolicy_Violation_SavesTheViolationAsEvidence()
    {
        var (passed, _, store) = VerifyLostParent();
        var evidence = Assert.ContainsSingle(passed.ResultFiles!);
        Assert.Contains("names parent 00000000000000ff", store.Saved[evidence]);
    }

    [TestMethod]
    public void TelemetryPolicy_Violation_LeavesAFailedResultWithoutAnAddedException()
    {
        var (_, failed, _) = VerifyLostParent();
        Assert.IsNull(failed.TestFailureException);
    }

    [TestMethod]
    public void TelemetryPolicy_Violation_LeavesAFailedResultWithoutEvidence()
    {
        var (_, failed, _) = VerifyLostParent();
        Assert.IsNull(failed.ResultFiles);
    }

    [TestMethod]
    public void TelemetryPolicy_AllSatisfied_KeepsThePassingOutcome()
    {
        Assert.AreEqual(UnitTestOutcome.Passed, VerifySatisfied().Outcome);
    }

    [TestMethod]
    public void TelemetryPolicy_AllSatisfied_AddsNoFailure()
    {
        Assert.IsNull(VerifySatisfied().TestFailureException);
    }

    [TestMethod]
    public void TelemetryPolicy_ClassAndMethodPolicies_AreCombinedByType()
    {
        Assert.AreSequenceEqual(["CapturesExactly", "ConnectedTrace", "NoErrorTelemetry"],
            DeclaredOnOverrides().Select(policy => policy.Name).Order(StringComparer.Ordinal));
    }

    [TestMethod]
    public void TelemetryPolicy_MethodPolicyOfAClassPolicyType_ReplacesTheClassPolicy()
    {
        Assert.AreSequenceEqual(["http.request.header.b"], Assert.ContainsSingle(DeclaredOnOverrides().OfType<CapturesExactlyAttribute>()).Fields);
    }

    private static (TestResult Passed, TestResult Failed, InMemoryEvidenceStore Store) VerifyLostParent()
    {
        var store = new InMemoryEvidenceStore();
        using var invocation = TelemetryInvocation.Begin([new ConnectedTraceAttribute()]);
        TelemetryInvocation.Publish(Published(Span(Server, Missing)));
        var passed = new TestResult { Outcome = UnitTestOutcome.Passed };
        var failed = new TestResult { Outcome = UnitTestOutcome.Failed };
        invocation.Verify([passed, failed], store);
        return (passed, failed, store);
    }

    private static TestResult VerifySatisfied()
    {
        using var invocation = TelemetryInvocation.Begin([new ConnectedTraceAttribute()]);
        TelemetryInvocation.Publish(Published(Span(Server, Root)));
        var passed = new TestResult { Outcome = UnitTestOutcome.Passed };
        invocation.Verify([passed], new InMemoryEvidenceStore());
        return passed;
    }

    private static IReadOnlyList<TelemetryPolicyAttribute> DeclaredOnOverrides() =>
        TelemetryPolicyAttribute.DeclaredOn(typeof(DeclaringCases).GetMethod(nameof(DeclaringCases.Overrides))!);

    private static SpanRecord StatusCodeAsString() =>
        Span(Server, Root, attributes: new Dictionary<string, object?> { ["http.response.status_code"] = "200" });

    private static SpanRecord DeprecatedDbSystem() =>
        Span(Server, Root, attributes: new Dictionary<string, object?> { ["db.system"] = "postgresql" });

    private static SpanRecord StatusSpan() =>
        Span(Server, Root, attributes: new Dictionary<string, object?> { ["http.response.status_code"] = 200L });

    private static ContractAttribute IntegerStatus =>
        Attribute("http.response.status_code", new AttributeValue.ObjectValue(new AttributeIntValue { Value = 200 }));

    private static PublishedTrace WithReceipts(params ExportReceipt[] receipts) =>
        new(new TraceEvidence(Trace, new TelemetrySnapshot([Span(Server, Root)], [], [], receipts)), Root);

    private static SpanRecord Failed(bool withException) => new(Trace, Server, "GET /orders")
    {
        ParentSpanId = Root,
        Kind = SpanKind.Server,
        Status = ActivityStatusCode.Error,
        StatusDescription = "boom",
        Scope = new InstrumentationScopeRecord { Name = "Microsoft.AspNetCore" },
        Resource = new ResourceRecord(new Dictionary<string, object?> { ["service.name"] = "orders" }),
        Events = withException
            ?
            [
                new SpanEventRecord("exception", 5, new Dictionary<string, object?>
                {
                    ["exception.type"] = "System.InvalidOperationException",
                    ["exception.message"] = "boom"
                })
            ]
            : []
    };

    private static IReadOnlyList<string> Violations(TelemetryPolicyAttribute policy, params SpanRecord[] spans) =>
        Violations(policy, Published(spans));

    private static IReadOnlyList<string> Violations(TelemetryPolicyAttribute policy, params PublishedTrace[] traces)
    {
        using var invocation = TelemetryInvocation.Begin([policy]);
        foreach (var trace in traces) TelemetryInvocation.Publish(trace);
        return invocation.Violations();
    }

    private static PublishedTrace Published(params SpanRecord[] spans) =>
        new(new TraceEvidence(Trace, new TelemetrySnapshot(spans, [], [], [])), Root);

    private static SpanRecord Span(string id, string parent, SpanKind kind = SpanKind.Server, string name = "GET /orders",
        string scope = "Microsoft.AspNetCore", IReadOnlyDictionary<string, object?>? attributes = null) => new(Trace, id, name)
    {
        ParentSpanId = parent,
        Kind = kind,
        StartTimeUnixNano = 1,
        EndTimeUnixNano = 10,
        Scope = new InstrumentationScopeRecord { Name = scope },
        Attributes = attributes ?? new Dictionary<string, object?>(),
        Resource = new ResourceRecord(new Dictionary<string, object?> { ["service.name"] = "orders" })
    };

    private static LogRecord Log(int severity, string scope, string? traceId) => new(
        new ResourceRecord(new Dictionary<string, object?> { ["service.name"] = "orders" }),
        new InstrumentationScopeRecord { Name = scope }, 1, 1, severity, severity >= 17 ? "Error" : "Information",
        null, new Dictionary<string, object?>(), 0, 0, traceId, null, "ConnectionError");

    private static ContractAttribute Attribute(string key, AttributeValue value) => new() { Key = key, Value = value };

    private static StoredSpan Stored(SpanRecord span, string name, params ContractAttribute[] attributes) =>
        Stored(span, name, null, attributes);

    private static StoredSpan Stored(SpanRecord span, string name, ContractAttribute[]? resource,
        params ContractAttribute[] attributes) => new()
    {
        TraceId = span.TraceId,
        SpanId = span.SpanId,
        ParentSpanId = span.ParentSpanId,
        Name = name,
        Kind = Enum.Parse<ContractKind>(span.Kind.ToString()),
        StartTimeUnixNano = span.StartTimeUnixNano,
        EndTimeUnixNano = span.EndTimeUnixNano,
        Status = new SpanStatus { Code = Enum.Parse<ContractStatus>(span.Status.ToString()) },
        Resource = new Resource { ServiceName = "orders", Attributes = resource ?? [Attribute("service.name", new AttributeValue.StringValue("orders"))] },
        InstrumentationScope = new InstrumentationScope { ScopeName = span.Scope?.Name },
        Attributes = attributes
    };

    [CapturesExactly("http.request.header.a")]
    [NoErrorTelemetry]
    private sealed class DeclaringCases
    {
        [CapturesExactly("http.request.header.b")]
        [ConnectedTrace]
        public static void Overrides()
        {
        }
    }
}

/// <summary>A plain test class never runs its policies, so publishing evidence from it must fail loudly.</summary>
[TestClass]
public sealed class UnverifiedPolicyTests
{
    [TestMethod]
    [ConnectedTrace]
    public void TelemetryPolicy_EvidenceFromPlainTestClassWithPolicies_Fails()
    {
        var trace = new PublishedTrace(new TraceEvidence("00112233445566778899aabbccddeeff", new TelemetrySnapshot([], [], [], [])), "0000000000000001");
        var error = Assert.ThrowsExactly<InvalidOperationException>(() => TelemetryInvocation.Publish(trace));
        Assert.AreEqual("Qyl.Telemetry.UnitTests.Policies.UnverifiedPolicyTests declares [ConnectedTrace], but only a "
            + "[TracePerTest] class verifies telemetry policies. Replace [TestClass] with [TracePerTest].", error.Message);
    }
}
