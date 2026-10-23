using System.Diagnostics;
using System.Runtime.CompilerServices;
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
[TestCategory("Fast")]
public class TelemetryPolicyTests
{
    private const string Root = "0000000000000001";
    private const string Server = "0000000000000002";
    private const string Client = "0000000000000003";
    private const string Missing = "00000000000000ff";
    private const string Selected = "http.request.header.x-request-id";
    private const string DeprecatedGrpcMetadata = "rpc.grpc.request.metadata.authorization";
    private const string AuthorizationHeader = "http.request.header.authorization";

    [TestMethod]
    [DynamicData(nameof(EveryPolicy))]
    public void Should_FailThePolicy_When_TelemetryPolicy_NoTracePublished(TelemetryPolicyAttribute policy)
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
    public void Should_Pass_When_ConnectedTrace_SpansUnderCapturedParentsOrTheTestRoot()
    {
        Assert.IsEmpty(Violations(new ConnectedTraceAttribute(), Span(Server, Root), Span(Client, Server, SpanKind.Client)));
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void Should_RejectTheCycle_When_ParentageCannotReachTheRoot(bool selfParent)
    {
        var spans = selfParent ? new[] { Span(Server, Server) } : [Span(Server, Client), Span(Client, Server)];
        Assert.AreSequenceEqual(spans.Select(span => $"[ConnectedTrace] 'GET /orders' (Server, orders, Microsoft.AspNetCore) "
            + $"has cyclic parentage in trace {span.TraceId}."), Violations(new ConnectedTraceAttribute(), spans));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Should_ReportTheMissingParent_When_AnotherObservationOfTheSameIdentityIsConnected(bool reverse)
    {
        var connected = Span(Server, Root);
        var disconnected = Span(Server, Missing);
        Assert.AreSequenceEqual(["[ConnectedTrace] 'GET /orders' (Server, orders, Microsoft.AspNetCore) names parent "
            + "00000000000000ff, which is neither a captured span nor the test root."],
            Violations(new ConnectedTraceAttribute(), reverse ? [disconnected, connected] : [connected, disconnected]));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Should_ReportTheSelfCycle_When_AnotherObservationOfTheSameIdentityIsConnected(bool reverse)
    {
        var connected = Span(Server, Root);
        var cyclic = Span(Server, Server);
        Assert.AreSequenceEqual([$"[ConnectedTrace] 'GET /orders' (Server, orders, Microsoft.AspNetCore) has cyclic parentage in trace {TestSpan.TraceId}."],
            Violations(new ConnectedTraceAttribute(), reverse ? [cyclic, connected] : [connected, cyclic]));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Should_ReportTheCycleAndItsDescendants_When_AnAlternateParentObservationClosesTheCycle(bool reverse)
    {
        var connected = Span(Server, Root);
        var alternate = Span(Server, Client, name: "alternate-parent");
        var peer = Span(Client, Server, name: "cycle-peer");
        var child = Span("0000000000000004", Client, name: "cycle-child");
        SpanRecord[] spans = reverse ? [child, peer, alternate, connected] : [connected, alternate, peer, child];
        Assert.AreSequenceEqual(new[] { alternate, peer, child }
                .Select(span => $"[ConnectedTrace] '{span.Name}' (Server, orders, Microsoft.AspNetCore) has cyclic parentage in trace {TestSpan.TraceId}."),
            Violations(new ConnectedTraceAttribute(), spans), SequenceOrder.InAnyOrder);
    }

    [TestMethod]
    public void Should_AcceptEveryParentObservation_When_AlternateParentsAllReachTheTestRoot()
    {
        Assert.IsEmpty(Violations(new ConnectedTraceAttribute(), Span(Server, Root), Span(Server, Client), Span(Client, Root)));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Should_AcceptConnectedRetransmissions_When_ObservationsShareAnIdentity(bool sharedReference)
    {
        var connected = Span(Server, Root);
        Assert.IsEmpty(Violations(new ConnectedTraceAttribute(), connected, sharedReference ? connected : Span(Server, Root)));
    }

    [TestMethod]
    public void Should_ShowEveryCyclicSpan_When_RenderingTheEvidence()
    {
        using var invocation = TelemetryInvocation.Begin([new ConnectedTraceAttribute()]);
        TelemetryInvocation.Publish(Published(Span(Server, Client), Span(Client, Server)));
        var outline = TelemetryReport.Outline(invocation);
        Assert.Contains($"span {Server}", outline);
        Assert.Contains($"span {Client}", outline);
        Assert.Contains("unrooted component (cyclic parentage)", outline);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Should_CountEveryObservation_When_RenderingRepeatedSpanIdentities(bool reverse)
    {
        var first = Span(Server, Root);
        var second = Span(Server, Root, name: "second-observation");
        var report = Render(reverse ? [second, first] : [first, second]);
        Assert.Contains($"trace {TestSpan.TraceId}, test root {Root}, 2 span(s)", report);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Should_RenderEveryPayload_When_DistinctObservationsShareTheSameIdentity(bool reverse)
    {
        var first = Span(Server, Root, attributes: new Dictionary<string, object?> { ["first.attribute"] = "first-payload" });
        var second = Span(Server, Root, name: "second-observation",
            attributes: new Dictionary<string, object?> { ["second.attribute"] = "second-payload" });
        var report = Render(reverse ? [second, first] : [first, second]);
        Assert.Contains("GET /orders  Server", report);
        Assert.Contains("second-observation  Server", report);
        Assert.Contains("first.attribute = \"first-payload\"", report);
        Assert.Contains("second.attribute = \"second-payload\"", report);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Should_RenderEachOccurrenceOnce_When_ConflictingObservationsFormACycle(bool reverse)
    {
        var connected = Span(Server, Root);
        var cyclic = Span(Server, Client, name: "cyclic-observation",
            attributes: new Dictionary<string, object?> { ["cyclic.attribute"] = "cycle-payload" });
        var peer = Span(Client, Server, name: "cycle-peer");
        var report = Render(reverse ? [peer, cyclic, connected] : [connected, cyclic, peer]);
        Assert.HasCount(3, report.Split(Environment.NewLine).Where(line => line.Contains("  span ", StringComparison.Ordinal)));
        Assert.Contains("cyclic-observation  Server", report);
        Assert.Contains("cyclic.attribute = \"cycle-payload\"", report);
    }

    [TestMethod]
    public void Should_RenderStructuredMetadata_When_AnObservationHasScopeAndEventPayloads()
    {
        var captured = Span(Server, Root, statusDescription: "observation-description", events: [EventWithValue(7)],
            scopeMetadata: new InstrumentationScopeRecord
            {
                Name = "Microsoft.AspNetCore", Version = "2.0", SchemaUrl = "https://example.test/schema/2",
                Attributes = new Dictionary<string, object?> { ["qyl.scope.value"] = new object?[] { "scope-payload", 8L } }
            });
        var report = Render(captured);
        Assert.Contains("status Unset: \"observation-description\"", report);
        Assert.Contains("scope version \"2.0\", schema \"https://example.test/schema/2\"", report);
        Assert.Contains("scope qyl.scope.value = [\"scope-payload\", 8]", report);
        Assert.Contains("event repeated at 1 {qyl.event.value: [\"expected\", 7]}", report);
    }

    [TestMethod]
    public void Should_BeNamedInTheFailure_When_ConnectedTrace_LostParent()
    {
        Assert.AreSequenceEqual(
        [
            "[ConnectedTrace] 'GET /orders' (Server, orders, Microsoft.AspNetCore) names parent 00000000000000ff, "
            + "which is neither a captured span nor the test root."
        ], Violations(new ConnectedTraceAttribute(), Span(Server, Missing)));
    }

    [TestMethod]
    public void Should_BeNamedInTheFailure_When_ConnectedTrace_SecondRoot()
    {
        Assert.AreSequenceEqual(
        [
            "[ConnectedTrace] 'GET /orders' (Client, orders, Microsoft.AspNetCore) starts a second root in trace "
            + "00112233445566778899aabbccddeeff."
        ], Violations(new ConnectedTraceAttribute(), Span(Server, Root), Span(Client, "", SpanKind.Client)));
    }

    [TestMethod]
    public void Should_BeNamedInTheFailure_When_ConnectedTrace_EmptyTrace()
    {
        var empty = new PublishedTrace(new TraceEvidence(TestSpan.TraceId, TestSpan.Only()), Root);
        Assert.AreSequenceEqual(["[ConnectedTrace] Trace 00112233445566778899aabbccddeeff exported no span."],
            Violations(new ConnectedTraceAttribute(), empty));
    }

    [TestMethod]
    public void Should_BeNamedInTheFailure_When_NoDuplicateSpans_RepeatedExport()
    {
        Assert.AreSequenceEqual(
            ["[NoDuplicateSpans] 'GET /orders' (Server, orders, Microsoft.AspNetCore) was exported 2 times as span 0000000000000002."],
            Violations(new NoDuplicateSpansAttribute(), Span(Server, Root), Span(Server, Root)));
    }

    [TestMethod]
    public void Should_CountBothExports_When_IdenticalObservationsArePublishedInSeparateCaptures()
    {
        Assert.AreSequenceEqual(
            ["[NoDuplicateSpans] 'GET /orders' (Server, orders, Microsoft.AspNetCore) was exported 2 times as span 0000000000000002."],
            Violations(new NoDuplicateSpansAttribute(), Published(Span(Server, Root)), Published(Span(Server, Root))));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Should_CountBothExports_When_ConflictingObservationsArePublishedInSeparateCaptures(bool reverse)
    {
        var original = Span(Server, Root);
        var changed = Span(Server, Root, name: "changed.operation");
        var first = reverse ? changed : original;
        var second = reverse ? original : changed;
        Assert.AreSequenceEqual([$"[NoDuplicateSpans] '{first.Name}' (Server, orders, Microsoft.AspNetCore) was exported 2 times as span {Server}."],
            Violations(new NoDuplicateSpansAttribute(), Published(first), Published(second)));
    }

    [TestMethod]
    public void Should_CountOneExport_When_OverlappingCapturesReuseTheSameObservation()
    {
        var server = Span(Server, Root);
        var client = Span(Client, Server, SpanKind.Client, "POST");
        Assert.IsEmpty(Violations(new NoDuplicateSpansAttribute(), Published(server), Published(server, client)));
    }

    [TestMethod]
    public void Should_KeepTraceIdentitiesSeparate_When_DifferentTracesReuseTheSameSpanId()
    {
        var other = Span(Server, Root, traceId: TestSpan.UnrelatedTraceId);
        Assert.IsEmpty(Violations(new NoDuplicateSpansAttribute(), Published(Span(Server, Root)), PublishedFor(other)));
    }

    [TestMethod]
    public void Should_ReportTheWrappedOperation_When_ParentAndChildArePublishedInSeparateCaptures()
    {
        Assert.AreSequenceEqual(["[NoDuplicateSpans] 'GET /orders' (Server, orders, Contrib.AspNetCore) repeats its parent "
            + "'GET /orders' (Server, orders, Microsoft.AspNetCore): the operation is instrumented twice."],
            Violations(new NoDuplicateSpansAttribute(), Published(Span(Server, Root)),
                Published(Span(Client, Server, scope: "Contrib.AspNetCore"))));
    }

    [TestMethod]
    public void Should_ReportConcurrentInstrumentation_When_ScopesArePublishedInSeparateCaptures()
    {
        Assert.AreSequenceEqual(["[NoDuplicateSpans] 'GET /orders' (Server, orders, Microsoft.AspNetCore) and 'GET /orders' (Server, orders, "
            + "Contrib.AspNetCore) describe the same operation concurrently: the operation is instrumented twice."],
            Violations(new NoDuplicateSpansAttribute(), Published(Span(Server, Root)),
                Published(Span(Client, Root, scope: "Contrib.AspNetCore"))));
    }

    [TestMethod]
    public void Should_BeNamedInTheFailure_When_NoDuplicateSpans_WrappedOperation()
    {
        Assert.AreSequenceEqual(
        [
            "[NoDuplicateSpans] 'GET /orders' (Server, orders, Contrib.AspNetCore) repeats its parent "
            + "'GET /orders' (Server, orders, Microsoft.AspNetCore): the operation is instrumented twice."
        ], Violations(new NoDuplicateSpansAttribute(), Span(Server, Root), Span(Client, Server, scope: "Contrib.AspNetCore")));
    }

    [TestMethod]
    public void Should_BeNamedInTheFailure_When_NoDuplicateSpans_ConcurrentSecondScope()
    {
        Assert.AreSequenceEqual(
        [
            "[NoDuplicateSpans] 'GET /orders' (Server, orders, Microsoft.AspNetCore) and 'GET /orders' (Server, orders, "
            + "Contrib.AspNetCore) describe the same operation concurrently: the operation is instrumented twice."
        ], Violations(new NoDuplicateSpansAttribute(), Span(Server, Root), Span("0000000000000004", Root, scope: "Contrib.AspNetCore")));
    }

    [TestMethod]
    public void Should_Pass_When_NoDuplicateSpans_LayeredGrpcOverHttp()
    {
        var grpc = Span(Client, Root, SpanKind.Client, "orders.Orders/Create");
        var http = Span("0000000000000004", Client, SpanKind.Client, "POST");
        Assert.IsEmpty(Violations(new NoDuplicateSpansAttribute(), grpc, http));
    }

    [TestMethod]
    public void Should_Pass_When_NoDuplicateSpans_ParallelCallsOfOneScope()
    {
        var first = Span("0000000000000005", Root, SpanKind.Client);
        var second = Span("0000000000000006", Root, SpanKind.Client);
        Assert.IsEmpty(Violations(new NoDuplicateSpansAttribute(), first, second));
    }

    [TestMethod]
    public void Should_BeReportedAsInvalid_When_CapturesExactly_NonHeaderField()
    {
        Assert.AreSequenceEqual(
            ["[CapturesExactly] 'url.path' is not an HTTP header or RPC metadata attribute."],
            Violations(new CapturesExactlyAttribute("url.path"), Span(Server, Root)));
    }

    [TestMethod]
    public void Should_BeReportedAsLost_When_CapturesExactly_SelectedFieldNeverCaptured()
    {
        Assert.AreSequenceEqual(
            ["[CapturesExactly] 'http.request.header.x-request-id' was selected, but no span captured it."],
            Violations(new CapturesExactlyAttribute(Selected), Span(Server, Root)));
    }

    [TestMethod]
    [DataRow(AuthorizationHeader)]
    [DataRow(DeprecatedGrpcMetadata)]
    public void Should_BeReported_When_CapturesExactly_UnselectedCapturedField(string field)
    {
        var server = Span(Server, Root, attributes: new Dictionary<string, object?> { [field] = new object?[] { "secret" } });
        Assert.AreSequenceEqual(
        [
            $"[CapturesExactly] 'GET /orders' (Server, orders, Microsoft.AspNetCore) captured unselected '{field}'."
        ], Violations(new CapturesExactlyAttribute(), server));
    }

    [TestMethod]
    public void Should_Pass_When_CapturesExactly_ExactSelection()
    {
        var selected = Span(Server, Root, attributes: new Dictionary<string, object?> { [Selected] = new object?[] { "request-1" } });
        Assert.IsEmpty(Violations(new CapturesExactlyAttribute(Selected), selected));
    }

    [TestMethod]
    public void Should_Pass_When_CapturesExactly_EmptySelectionWithoutFields()
    {
        Assert.IsEmpty(Violations(new CapturesExactlyAttribute(), Span(Server, Root)));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Should_RejectTheUnselectedField_When_APriorObservationReusesTheSpanId(bool sameTrace)
    {
        var second = Span(Server, Root, attributes: new Dictionary<string, object?> { [AuthorizationHeader] = "secret" },
            traceId: sameTrace ? TestSpan.TraceId : TestSpan.UnrelatedTraceId);
        Assert.AreSequenceEqual([$"[CapturesExactly] 'GET /orders' (Server, orders, Microsoft.AspNetCore) captured unselected '{AuthorizationHeader}'."],
            Violations(new CapturesExactlyAttribute(), Published(Span(Server, Root)), PublishedFor(second)));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Should_AcceptTheSelectedField_When_APriorObservationReusesTheSpanId(bool sameTrace)
    {
        var second = Span(Server, Root, attributes: new Dictionary<string, object?> { [Selected] = new object?[] { "request" } },
            traceId: sameTrace ? TestSpan.TraceId : TestSpan.UnrelatedTraceId);
        Assert.IsEmpty(Violations(new CapturesExactlyAttribute(Selected), Published(Span(Server, Root)), PublishedFor(second)));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Should_BeLocatedAsTypeViolation_When_ConformsToSemanticConventions_StringStatusCode(bool allowDeprecated)
    {
        Assert.AreSequenceEqual(
        [
            "[ConformsToSemanticConventions] spans[0] 'GET /orders' (Server, orders, Microsoft.AspNetCore) "
            + "'http.response.status_code': Expected Integer."
        ], Violations(new ConformsToSemanticConventionsAttribute { AllowDeprecated = allowDeprecated }, StatusCodeAsString()));
    }

    [TestMethod]
    public void Should_BeLocated_When_ConformsToSemanticConventions_DeprecatedAttribute()
    {
        Assert.AreSequenceEqual(
        [
            "[ConformsToSemanticConventions] spans[0] 'GET /orders' (Server, orders, Microsoft.AspNetCore) "
            + "'db.system': The pinned registry marks this attribute deprecated."
        ], Violations(new ConformsToSemanticConventionsAttribute(), DeprecatedDbSystem()));
    }

    [TestMethod]
    public void Should_Pass_When_ConformsToSemanticConventions_DeprecatedAttributeWithAllowDeprecated()
    {
        Assert.IsEmpty(Violations(new ConformsToSemanticConventionsAttribute { AllowDeprecated = true }, DeprecatedDbSystem()));
    }

    [TestMethod]
    public void Should_Pass_When_ConformsToSemanticConventions_CustomAttribute()
    {
        var server = Span(Server, Root, attributes: new Dictionary<string, object?> { ["orders.custom"] = 7L });
        Assert.IsEmpty(Violations(new ConformsToSemanticConventionsAttribute(), server));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Should_RequireExplicitDefinitionConformance_When_ConformsToSemanticConventions_ContextDependentType(bool allowDeprecated)
    {
        var span = Span(Server, Root, kind: SpanKind.Internal, name: "codex.exec", scope: "OpenAI.Codex",
            attributes: new Dictionary<string, object?> { ["thread.id"] = "0199a213-81c0-7800-8aa1-bbab2a035a53" });
        Assert.AreSequenceEqual(
        [
            "[ConformsToSemanticConventions] spans[0] 'codex.exec' (Internal, orders, OpenAI.Codex) "
            + "'thread.id': The registered value type depends on the selected signal definition; run explicit definition conformance."
        ], Violations(new ConformsToSemanticConventionsAttribute { AllowDeprecated = allowDeprecated }, span));
    }

    [TestMethod]
    public void Should_BeReported_When_NoErrorTelemetry_ErrorStatusOnTraceSpan()
    {
        Assert.AreSequenceEqual(
            ["[NoErrorTelemetry] 'GET /orders' (Server, orders, Microsoft.AspNetCore) ended with error status: boom"],
            Violations(new NoErrorTelemetryAttribute(), Published(Failed(withException: false))));
    }

    [TestMethod]
    public void Should_BeReported_When_NoErrorTelemetry_ExceptionEventOnTraceSpan()
    {
        Assert.Contains("[NoErrorTelemetry] 'GET /orders' (Server, orders, Microsoft.AspNetCore) recorded "
            + "\"System.InvalidOperationException\": \"boom\"",
            Violations(new NoErrorTelemetryAttribute(), Published(Failed(withException: true))));
    }

    [TestMethod]
    public void Should_BeReported_When_NoErrorTelemetry_ErrorLogWithoutTrace()
    {
        var capture = new TelemetrySnapshot([Span(Server, Root)], [Log(17, "Microsoft.AspNetCore.Server.Kestrel", null)], [], []);
        Assert.AreSequenceEqual(
            ["[NoErrorTelemetry] orders logged Error from Microsoft.AspNetCore.Server.Kestrel: ConnectionError"],
            Violations(new NoErrorTelemetryAttribute(), new PublishedTrace(new TraceEvidence(TestSpan.TraceId, capture), Root)));
    }

    [TestMethod]
    public void Should_Pass_When_NoErrorTelemetry_InformationLogOfTheTrace()
    {
        var capture = new TelemetrySnapshot([Span(Server, Root)], [Log(13, "Orders", TestSpan.TraceId)], [], []);
        Assert.IsEmpty(Violations(new NoErrorTelemetryAttribute(), new PublishedTrace(new TraceEvidence(TestSpan.TraceId, capture), Root)));
    }

    [TestMethod]
    public void Should_Pass_When_NoErrorTelemetry_ErrorFixtureSpanOfAnotherTrace()
    {
        var healthProbe = new SpanRecord(TestSpan.UnrelatedTraceId, Client, "GET")
        {
            Kind = SpanKind.Client,
            Status = ActivityStatusCode.Error
        };
        var capture = TestSpan.Only(Span(Server, Root), healthProbe);
        Assert.IsEmpty(Violations(new NoErrorTelemetryAttribute(), new PublishedTrace(new TraceEvidence(TestSpan.TraceId, capture), Root)));
    }

    [TestMethod]
    public void Should_Pass_When_NoErrorTelemetry_HealthySpan()
    {
        Assert.IsEmpty(Violations(new NoErrorTelemetryAttribute(), Span(Server, Root)));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Should_ReportTheError_When_APriorObservationReusesTheSpanId(bool sameTrace)
    {
        var second = Failed(withException: false, sameTrace ? TestSpan.TraceId : TestSpan.UnrelatedTraceId);
        Assert.AreSequenceEqual(["[NoErrorTelemetry] 'GET /orders' (Server, orders, Microsoft.AspNetCore) ended with error status: boom"],
            Violations(new NoErrorTelemetryAttribute(), Published(Span(Server, Root)), PublishedFor(second)));
    }

    [TestMethod]
    public void Should_BeReported_When_ExportsCleanly_ReceiverWarning()
    {
        Assert.AreSequenceEqual(["[ExportsCleanly] A HttpProtobuf logs export: Coalesced identical OTLP attribute: ConnectionId."],
            Violations(new ExportsCleanlyAttribute(), WithReceipts(new ExportReceipt("logs", 1, 0, null) { Warnings = ["Coalesced identical OTLP attribute: ConnectionId."] })));
    }

    [TestMethod]
    public void Should_BeReportedWithTheError_When_ExportsCleanly_RejectedRecords()
    {
        Assert.AreSequenceEqual(["[ExportsCleanly] A Grpc traces export rejected 2 record(s): invalid span id"],
            Violations(new ExportsCleanlyAttribute(), WithReceipts(new ExportReceipt("traces", 1, 2, "invalid span id") { Transport = OtlpTransport.Grpc })));
    }

    [TestMethod]
    public void Should_Pass_When_ExportsCleanly_CleanExport()
    {
        Assert.IsEmpty(Violations(new ExportsCleanlyAttribute(), WithReceipts(new ExportReceipt("metrics", 3, 0, null))));
    }

    [TestMethod]
    public void Should_Fail_When_ExportsCleanly_NoExportRecorded()
    {
        Assert.AreSequenceEqual(["[ExportsCleanly] The capture recorded no export."],
            Violations(new ExportsCleanlyAttribute(), Span(Server, Root)));
    }

    [TestMethod]
    public void Should_Pass_When_PreservedByCollector_IdenticalStorageWithPolicyDeniedKeysWithheld()
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
    public void Should_BeNamedInTheFailure_When_PreservedByCollector_RenamedSpan()
    {
        var captured = StatusSpan();
        Assert.AreSequenceEqual(["[PreservedByCollector] 'GET /orders' (Server, orders, Microsoft.AspNetCore) was stored as 'GET'."],
            Violations(new PreservedByCollectorAttribute(), Published(captured) with { Stored = [Stored(captured, "GET", IntegerStatus)] }));
    }

    [TestMethod]
    public void Should_BeNamedInTheFailure_When_PreservedByCollector_ChangedValueType()
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
    public void Should_BeNamedInTheFailure_When_PreservedByCollector_RetainedPolicyDeniedKey()
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
    public void Should_BeNamedInTheFailure_When_PreservedByCollector_LostAttribute()
    {
        var captured = Span(Server, Root, attributes: new Dictionary<string, object?> { ["url.path"] = "/orders" });
        Assert.AreSequenceEqual(["[PreservedByCollector] 'GET /orders' (Server, orders, Microsoft.AspNetCore) lost 'url.path' = \"/orders\"."],
            Violations(new PreservedByCollectorAttribute(), Published(captured) with { Stored = [Stored(captured, captured.Name)] }));
    }

    [TestMethod]
    public void Should_BeNamedInTheFailure_When_PreservedByCollector_GainedAttribute()
    {
        var captured = Span(Server, Root);
        Assert.AreSequenceEqual(["[PreservedByCollector] 'GET /orders' (Server, orders, Microsoft.AspNetCore) gained 'qyl.added' = true."],
            Violations(new PreservedByCollectorAttribute(), Published(captured) with
            {
                Stored = [Stored(captured, captured.Name, Attribute("qyl.added", new AttributeValue.BoolValue(true)))]
            }));
    }

    [TestMethod]
    public void Should_BeNamedInTheFailure_When_PreservedByCollector_LostResourceAttribute()
    {
        var captured = Span(Server, Root);
        Assert.AreSequenceEqual(["[PreservedByCollector] 'GET /orders' (Server, orders, Microsoft.AspNetCore) resource lost 'service.name' = \"orders\"."],
            Violations(new PreservedByCollectorAttribute(), Published(captured) with { Stored = [Stored(captured, captured.Name, resource: [])] }));
    }

    [TestMethod]
    public void Should_BeNamedInTheFailure_When_PreservedByCollector_CapturedSpanNotStored()
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
    public void Should_Fail_When_PreservedByCollector_TraceNotReadBack()
    {
        Assert.AreSequenceEqual(
        [
            "[PreservedByCollector] Trace 00112233445566778899aabbccddeeff was not read back from a Collector. "
            + "Capture it through a TelemetryPipeline root; TelemetryCapture.TraceAsync has no Collector."
        ], Violations(new PreservedByCollectorAttribute(), Span(Server, Root)));
    }

    [TestMethod]
    public void Should_ReportTheCapturedSpanMissing_When_ReadbackContainsAnotherTraceWithTheSameSpanId()
    {
        var captured = Span(Server, Root);
        var stored = StoredWithMetadata(captured, traceId: TestSpan.UnrelatedTraceId);
        Assert.AreSequenceEqual([$"[PreservedByCollector] 'GET /orders' (Server, orders, Microsoft.AspNetCore) span {Server} was captured but not stored."],
            Violations(new PreservedByCollectorAttribute(), Published(captured) with { Stored = [stored] }));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Should_ReportTheChangedCaptureName_When_AnotherObservationOfTheSameIdentityMatchesStorage(bool reverse)
    {
        var original = Span(Server, Root);
        var changed = Span(Server, Root, name: "changed.operation");
        var published = reverse ? Published(changed, original) : Published(original, changed);
        Assert.AreSequenceEqual(["[PreservedByCollector] 'changed.operation' (Server, orders, Microsoft.AspNetCore) was stored as 'GET /orders'."],
            Violations(new PreservedByCollectorAttribute(), published with { Stored = [Stored(original, original.Name)] }));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Should_ReportTheChangedStoredName_When_AnotherStoredCandidateOfTheSameIdentityMatchesCapture(bool reverse)
    {
        var captured = Span(Server, Root);
        var original = Stored(captured, captured.Name);
        var changed = Stored(captured, "changed.operation");
        Assert.AreSequenceEqual(["[PreservedByCollector] 'GET /orders' (Server, orders, Microsoft.AspNetCore) was stored as 'changed.operation'."],
            Violations(new PreservedByCollectorAttribute(), Published(captured) with { Stored = reverse ? [changed, original] : [original, changed] }));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Should_ReportTheChangedCapturedEventPayload_When_AnotherObservationOfTheSameIdentityMatchesStorage(bool reverse)
    {
        var original = Span(Server, Root, events: [EventWithValue(7)]);
        var changed = Span(Server, Root, events: [EventWithValue(8)]);
        var published = reverse ? Published(changed, original) : Published(original, changed);
        Assert.AreSequenceEqual(["[PreservedByCollector] 'GET /orders' (Server, orders, Microsoft.AspNetCore) event 'repeated' "
            + "changed 'qyl.event.value' from [\"expected\", 8] to [\"expected\", 7]."],
            Violations(new PreservedByCollectorAttribute(), published with
            {
                Stored = [StoredWithMetadata(original, events: [StoredEventWithValue(7)])]
            }));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Should_ReportTheChangedStoredEventPayload_When_AnotherStoredCandidateOfTheSameIdentityMatchesCapture(bool reverse)
    {
        var captured = Span(Server, Root, events: [EventWithValue(7)]);
        var original = StoredWithMetadata(captured, events: [StoredEventWithValue(7)]);
        var changed = StoredWithMetadata(captured, events: [StoredEventWithValue(8)]);
        Assert.AreSequenceEqual(["[PreservedByCollector] 'GET /orders' (Server, orders, Microsoft.AspNetCore) event 'repeated' "
            + "changed 'qyl.event.value' from [\"expected\", 7] to [\"expected\", 8]."],
            Violations(new PreservedByCollectorAttribute(), Published(captured) with { Stored = reverse ? [changed, original] : [original, changed] }));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Should_AcceptIdenticalRetransmissions_When_MultipleObservationsShareAnIdentity(bool sharedReference)
    {
        var captured = Span(Server, Root, events: [EventWithValue(7)]);
        var repeated = sharedReference ? captured : Span(Server, Root, events: [EventWithValue(7)]);
        var stored = StoredWithMetadata(captured, events: [StoredEventWithValue(7)]);
        Assert.IsEmpty(Violations(new PreservedByCollectorAttribute(), Published(captured, repeated) with
        {
            Stored = [stored, sharedReference ? stored : StoredWithMetadata(repeated, events: [StoredEventWithValue(7)])]
        }));
    }

    [TestMethod]
    public void Should_AcceptOneStoredCopy_When_IdenticalCapturesAreRetransmitted()
    {
        var first = Span(Server, Root, events: [EventWithValue(7)]);
        var second = Span(Server, Root, events: [EventWithValue(7)]);
        Assert.IsEmpty(Violations(new PreservedByCollectorAttribute(), Published(first, second) with
        {
            Stored = [StoredWithMetadata(first, events: [StoredEventWithValue(7)])]
        }));
    }

    [TestMethod]
    public void Should_AcceptSharedObservations_When_MultiplePublishedViewsContainTheSameCaptureAndStorage()
    {
        var captured = Span(Server, Root);
        var trace = Published(captured) with { Stored = [Stored(captured, captured.Name)] };
        Assert.IsEmpty(Violations(new PreservedByCollectorAttribute(), trace, trace));
    }

    [TestMethod]
    public void Should_MatchEachFullIdentity_When_DifferentTracesReuseTheSameSpanId()
    {
        var first = Span(Server, Root);
        var second = Span(Server, Root, name: "other.operation", traceId: TestSpan.UnrelatedTraceId);
        var stored = new[] { Stored(first, first.Name), Stored(second, second.Name) };
        Assert.IsEmpty(Violations(new PreservedByCollectorAttribute(), Published(first) with { Stored = stored },
            PublishedFor(second) with { Stored = stored }));
    }

    [TestMethod]
    public void Should_ReportTheLostOccurrence_When_TwoIdenticalEventsBecomeOne()
    {
        var captured = Span(Server, Root, events: [Event(), Event()]);
        var stored = StoredWithMetadata(captured, events: [StoredEvent()]);
        Assert.AreSequenceEqual(["[PreservedByCollector] 'GET /orders' (Server, orders, Microsoft.AspNetCore) lost event 'repeated' at 1."],
            Violations(new PreservedByCollectorAttribute(), Published(captured) with { Stored = [stored] }));
    }

    [TestMethod]
    public void Should_AcceptBothOccurrences_When_IdenticalEventsArePreserved()
    {
        var captured = Span(Server, Root, events: [Event(), Event()]);
        var stored = StoredWithMetadata(captured, events: [StoredEvent(), StoredEvent()]);
        Assert.IsEmpty(Violations(new PreservedByCollectorAttribute(), Published(captured) with { Stored = [stored] }));
    }

    [TestMethod]
    public void Should_ReportTheExtraOccurrence_When_OneEventBecomesTwo()
    {
        var captured = Span(Server, Root, events: [Event()]);
        var stored = StoredWithMetadata(captured, events: [StoredEvent(), StoredEvent()]);
        Assert.AreSequenceEqual(["[PreservedByCollector] 'GET /orders' (Server, orders, Microsoft.AspNetCore) gained event 'repeated' at 1."],
            Violations(new PreservedByCollectorAttribute(), Published(captured) with { Stored = [stored] }));
    }

    [TestMethod]
    public void Should_ReportTheLostDescription_When_StatusCodeIsPreserved()
    {
        var captured = Span(Server, Root, statusDescription: "explanation");
        Assert.AreSequenceEqual(["[PreservedByCollector] 'GET /orders' (Server, orders, Microsoft.AspNetCore) was stored with status description '' instead of 'explanation'."],
            Violations(new PreservedByCollectorAttribute(), Published(captured) with { Stored = [Stored(captured, captured.Name)] }));
    }

    [TestMethod]
    public void Should_ReportTheLostVersion_When_ScopeNameIsPreserved()
    {
        var captured = ScopeSpan(version: "1.0");
        Assert.AreSequenceEqual(["[PreservedByCollector] 'GET /orders' (Server, orders, Microsoft.AspNetCore) was stored with scope version '' instead of '1.0'."],
            Violations(new PreservedByCollectorAttribute(), Published(captured) with { Stored = [Stored(captured, captured.Name)] }));
    }

    [TestMethod]
    public void Should_ReportTheLostSchema_When_ScopeNameIsPreserved()
    {
        var captured = ScopeSpan(schema: "https://example.test/schema/1");
        Assert.AreSequenceEqual(["[PreservedByCollector] 'GET /orders' (Server, orders, Microsoft.AspNetCore) was stored with scope schema '' instead of 'https://example.test/schema/1'."],
            Violations(new PreservedByCollectorAttribute(), Published(captured) with { Stored = [Stored(captured, captured.Name)] }));
    }

    [TestMethod]
    public void Should_ReportTheLostAttribute_When_ScopeNameIsPreserved()
    {
        var captured = ScopeSpan(attributes: new Dictionary<string, object?> { ["qyl.scope.value"] = "expected" });
        Assert.AreSequenceEqual(["[PreservedByCollector] 'GET /orders' (Server, orders, Microsoft.AspNetCore) scope lost 'qyl.scope.value' = \"expected\"."],
            Violations(new PreservedByCollectorAttribute(), Published(captured) with { Stored = [Stored(captured, captured.Name)] }));
    }

    [TestMethod]
    public void Should_AcceptTheMetadata_When_StatusAndScopeArePreserved()
    {
        var captured = ScopeSpan("1.0", "https://example.test/schema/1",
            new Dictionary<string, object?> { ["qyl.scope.value"] = "expected" }, "explanation");
        var stored = StoredWithMetadata(captured,
            scope: new InstrumentationScope { ScopeName = "Microsoft.AspNetCore", ScopeVersion = "1.0", SchemaUrl = "https://example.test/schema/1",
                ScopeAttributes = [Attribute("qyl.scope.value", new AttributeValue.StringValue("expected"))] },
            status: new SpanStatus { Code = ContractStatus.Unset, Message = "explanation" });
        Assert.IsEmpty(Violations(new PreservedByCollectorAttribute(), Published(captured) with { Stored = [stored] }));
    }

    [TestMethod]
    public void Should_RejectTheDeniedCopy_When_AResourceKeyIsAddedToSpanAttributes()
    {
        var captured = Span(Server, Root, resource: new ResourceRecord(new Dictionary<string, object?>
            { ["service.name"] = "orders", ["session.id"] = "denied" }));
        var stored = Stored(captured, captured.Name, Attribute("session.id", new AttributeValue.StringValue("denied")));
        Assert.AreSequenceEqual(["[PreservedByCollector] 'GET /orders' (Server, orders, Microsoft.AspNetCore) retained 'session.id', which the persistence policy denies."],
            Violations(new PreservedByCollectorAttribute(), Published(captured) with { Stored = [stored] }));
    }

    [TestMethod]
    public void Should_FailThePassingResult_When_TelemetryPolicy_Violation()
    {
        var (passed, _, _) = VerifyLostParent();
        Assert.AreEqual(UnitTestOutcome.Failed, passed.Outcome);
    }

    [TestMethod]
    public void Should_StartTheFailureWithTheViolation_When_TelemetryPolicy_Violation()
    {
        var (passed, _, _) = VerifyLostParent();
        var failure = Assert.IsInstanceOfType<AssertFailedException>(passed.TestFailureException);
        Assert.StartsWith("Telemetry policy violated (1):" + Environment.NewLine + "- [ConnectedTrace] 'GET /orders'", failure.Message);
    }

    [TestMethod]
    public void Should_OutlineTheTraceInTheFailure_When_TelemetryPolicy_Violation()
    {
        var (passed, _, _) = VerifyLostParent();
        var failure = Assert.IsInstanceOfType<AssertFailedException>(passed.TestFailureException);
        Assert.Contains("GET /orders  Server  orders  Microsoft.AspNetCore  span 0000000000000002  "
            + "(parent 00000000000000ff was not captured)", failure.Message);
    }

    [TestMethod]
    public void Should_AttachTheEvidenceTheFailureCites_When_TelemetryPolicy_Violation()
    {
        var (passed, _, _) = VerifyLostParent();
        var failure = Assert.IsInstanceOfType<AssertFailedException>(passed.TestFailureException);
        var evidence = Assert.ContainsSingle(passed.ResultFiles!);
        Assert.EndsWith("Evidence: " + evidence, failure.Message);
    }

    [TestMethod]
    public void Should_SaveTheViolationAsEvidence_When_TelemetryPolicy_Violation()
    {
        var (passed, _, store) = VerifyLostParent();
        var evidence = Assert.ContainsSingle(passed.ResultFiles!);
        Assert.Contains("names parent 00000000000000ff", store.Saved[evidence]);
    }

    [TestMethod]
    public void Should_LeaveAFailedResultWithoutAnAddedException_When_TelemetryPolicy_Violation()
    {
        var (_, failed, _) = VerifyLostParent();
        Assert.IsNull(failed.TestFailureException);
    }

    [TestMethod]
    public void Should_LeaveAFailedResultWithoutEvidence_When_TelemetryPolicy_Violation()
    {
        var (_, failed, _) = VerifyLostParent();
        Assert.IsNull(failed.ResultFiles);
    }

    [TestMethod]
    public void Should_KeepThePassingOutcome_When_TelemetryPolicy_AllSatisfied()
    {
        Assert.AreEqual(UnitTestOutcome.Passed, VerifySatisfied().Outcome);
    }

    [TestMethod]
    public void Should_AddNoFailure_When_TelemetryPolicy_AllSatisfied()
    {
        Assert.IsNull(VerifySatisfied().TestFailureException);
    }

    [TestMethod]
    public void Should_BeCombinedByType_When_TelemetryPolicy_ClassAndMethodPolicies()
    {
        Assert.AreSequenceEqual(["CapturesExactly", "ConnectedTrace", "NoErrorTelemetry"],
            DeclaredOnOverrides().Select(policy => policy.Name).Order(StringComparer.Ordinal));
    }

    [TestMethod]
    public void Should_ReplaceTheClassPolicy_When_TelemetryPolicy_MethodPolicyOfAClassPolicyType()
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
        new(new TraceEvidence(TestSpan.TraceId, new TelemetrySnapshot([Span(Server, Root)], [], [], receipts)), Root);

    private static SpanRecord Failed(bool withException, string? traceId = null) => new(traceId ?? TestSpan.TraceId, Server, "GET /orders")
    {
        ParentSpanId = Root,
        Kind = SpanKind.Server,
        Status = ActivityStatusCode.Error,
        StatusDescription = "boom",
        Scope = new InstrumentationScopeRecord { Name = "Microsoft.AspNetCore" },
        Resource = TestSpan.Orders,
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

    private static string Render(params SpanRecord[] spans)
    {
        using var invocation = TelemetryInvocation.Begin([]);
        TelemetryInvocation.Publish(Published(spans));
        return TelemetryReport.Render(invocation, []);
    }

    private static IReadOnlyList<string> Violations(TelemetryPolicyAttribute policy, params PublishedTrace[] traces)
    {
        using var invocation = TelemetryInvocation.Begin([policy]);
        foreach (var trace in traces) TelemetryInvocation.Publish(trace);
        return invocation.Violations();
    }

    private static PublishedTrace Published(params SpanRecord[] spans) =>
        new(new TraceEvidence(TestSpan.TraceId, TestSpan.Only(spans)), Root);

    private static PublishedTrace PublishedFor(SpanRecord span) =>
        new(new TraceEvidence(span.TraceId, TestSpan.Only(span)), Root);

    private static SpanEventRecord Event() => new("repeated", 1, new Dictionary<string, object?>());

    private static SpanEvent StoredEvent() => new() { Name = "repeated", TimeUnixNano = 1, Attributes = [] };

    private static SpanEventRecord EventWithValue(long value) =>
        new("repeated", 1, new Dictionary<string, object?> { ["qyl.event.value"] = new object?[] { "expected", value } });

    private static SpanEvent StoredEventWithValue(long value) => new()
    {
        Name = "repeated", TimeUnixNano = 1,
        Attributes = [Attribute("qyl.event.value", new AttributeValue.ArrayValue(
            [new AttributeValue.StringValue("expected"), new AttributeValue.ObjectValue(new AttributeIntValue { Value = value })]))]
    };

    private static SpanRecord ScopeSpan(string? version = null, string? schema = null,
        IReadOnlyDictionary<string, object?>? attributes = null, string? description = null) =>
        Span(Server, Root, scopeMetadata: new InstrumentationScopeRecord
        {
            Name = "Microsoft.AspNetCore", Version = version, SchemaUrl = schema,
            Attributes = attributes ?? new Dictionary<string, object?>()
        }, statusDescription: description);

    private static SpanRecord Span(string id, string parent, SpanKind kind = SpanKind.Server, string name = "GET /orders",
        string scope = "Microsoft.AspNetCore", IReadOnlyDictionary<string, object?>? attributes = null,
        string? traceId = null, IReadOnlyList<SpanEventRecord>? events = null, string? statusDescription = null,
        InstrumentationScopeRecord? scopeMetadata = null, ResourceRecord? resource = null) => new(traceId ?? TestSpan.TraceId, id, name)
    {
        ParentSpanId = parent,
        Kind = kind,
        StartTimeUnixNano = 1,
        EndTimeUnixNano = 10,
        Scope = scopeMetadata ?? new InstrumentationScopeRecord { Name = scope },
        StatusDescription = statusDescription,
        Events = events ?? [],
        Attributes = attributes ?? new Dictionary<string, object?>(),
        Resource = resource ?? TestSpan.Orders
    };

    private static LogRecord Log(int severity, string scope, string? traceId) => new(
        TestSpan.Orders,
        new InstrumentationScopeRecord { Name = scope }, 1, 1, severity, severity >= 17 ? "Error" : "Information",
        null, new Dictionary<string, object?>(), 0, 0, traceId, null, "ConnectionError");

    private static ContractAttribute Attribute(string key, AttributeValue value) => new() { Key = key, Value = value };

    private static StoredSpan Stored(SpanRecord span, string name, params ContractAttribute[] attributes) =>
        Stored(span, name, null, attributes);

    private static StoredSpan Stored(SpanRecord span, string name, ContractAttribute[]? resource,
        params ContractAttribute[] attributes) => StoredWithMetadata(span, name, resource: resource, attributes: attributes);

    private static StoredSpan StoredWithMetadata(SpanRecord span, string? name = null,
        InstrumentationScope? scope = null, SpanStatus? status = null, IReadOnlyList<SpanEvent>? events = null,
        ContractAttribute[]? resource = null, ContractAttribute[]? attributes = null, string? traceId = null) => new()
    {
        TraceId = traceId ?? span.TraceId,
        SpanId = span.SpanId,
        ParentSpanId = span.ParentSpanId,
        Name = name ?? span.Name,
        Kind = Enum.Parse<ContractKind>(span.Kind.ToString()),
        StartTimeUnixNano = span.StartTimeUnixNano,
        EndTimeUnixNano = span.EndTimeUnixNano,
        Status = status ?? new SpanStatus { Code = Enum.Parse<ContractStatus>(span.Status.ToString()) },
        Resource = new Resource { ServiceName = "orders", Attributes = resource ?? [Attribute("service.name", new AttributeValue.StringValue("orders"))] },
        InstrumentationScope = scope ?? new InstrumentationScope { ScopeName = span.Scope?.Name },
        Attributes = attributes ?? [],
        Events = events
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
[TestCategory("Fast")]
public class UnverifiedPolicyTests
{
    [TestMethod]
    [ConnectedTrace]
    public void Should_Fail_When_TelemetryPolicy_EvidenceFromPlainTestClassWithPolicies()
    {
        var trace = new PublishedTrace(new TraceEvidence(TestSpan.TraceId, TestSpan.Only()), "0000000000000001");
        var error = Assert.ThrowsExactly<InvalidOperationException>(() => TelemetryInvocation.Publish(trace));
        var expected = RuntimeFeature.IsDynamicCodeSupported
            ? "Qyl.Telemetry.UnitTests.Policies.UnverifiedPolicyTests declares [ConnectedTrace], but only a "
              + "[TracePerTest] class verifies telemetry policies. Replace [TestClass] with [TracePerTest]."
            : "Telemetry published outside a [TracePerTest] invocation cannot verify policies in a NativeAOT test runner. "
              + "Use [TracePerTest] for telemetry publication.";
        Assert.AreEqual(expected, error.Message);
    }
}
