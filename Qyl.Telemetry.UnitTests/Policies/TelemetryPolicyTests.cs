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

    [TestMethod]
    public void Every_policy_fails_when_no_trace_was_published()
    {
        TelemetryPolicyAttribute[] policies =
        [
            new ConnectedTraceAttribute(), new NoDuplicateSpansAttribute(), new CapturesExactlyAttribute(),
            new ConformsToSemanticConventionsAttribute(), new PreservedByCollectorAttribute(),
            new NoErrorTelemetryAttribute(), new ExportsCleanlyAttribute()
        ];
        using var invocation = TelemetryInvocation.Begin(policies);
        Assert.AreSequenceEqual(policies.Select(policy => $"[{policy.Name}] No trace was published. Capture through "
            + "TelemetryCapture.TraceAsync or a TelemetryPipeline root inside the test."), invocation.Violations());
    }

    [TestMethod]
    public void Connected_trace_accepts_spans_under_captured_parents_or_the_test_root()
    {
        Assert.IsEmpty(Violations(new ConnectedTraceAttribute(), Span(Server, Root), Span(Client, Server, SpanKind.Client)));
    }

    [TestMethod]
    public void Connected_trace_names_a_lost_parent_a_second_root_and_an_empty_trace()
    {
        var empty = new PublishedTrace(new TraceEvidence(Trace, new TelemetrySnapshot([], [], [], [])), Root);
        Assert.AreSequenceEqual(
        [
            "[ConnectedTrace] 'GET /orders' (Server, orders, Microsoft.AspNetCore) names parent 00000000000000ff, "
            + "which is neither a captured span nor the test root.",
            "[ConnectedTrace] 'GET /orders' (Client, orders, Microsoft.AspNetCore) starts a second root in trace "
            + "00112233445566778899aabbccddeeff.",
            "[ConnectedTrace] Trace 00112233445566778899aabbccddeeff exported no span."
        ], Violations(new ConnectedTraceAttribute(), Published(Span(Server, Missing), Span(Client, "", SpanKind.Client)), empty));
    }

    [TestMethod]
    public void Duplicate_spans_name_a_repeated_export_a_wrapped_operation_and_a_concurrent_second_scope()
    {
        var wrapped = Span(Client, Server, scope: "Contrib.AspNetCore");
        var mirrored = Span("0000000000000004", Root, scope: "Contrib.AspNetCore");
        Assert.AreSequenceEqual(
        [
            "[NoDuplicateSpans] 'GET /orders' (Server, orders, Microsoft.AspNetCore) was exported 2 times as span 0000000000000002.",
            "[NoDuplicateSpans] 'GET /orders' (Server, orders, Contrib.AspNetCore) repeats its parent "
            + "'GET /orders' (Server, orders, Microsoft.AspNetCore): the operation is instrumented twice.",
            "[NoDuplicateSpans] 'GET /orders' (Server, orders, Microsoft.AspNetCore) and 'GET /orders' (Server, orders, "
            + "Contrib.AspNetCore) describe the same operation concurrently: the operation is instrumented twice."
        ], Violations(new NoDuplicateSpansAttribute(), Span(Server, Root), Span(Server, Root), wrapped, mirrored));
    }

    [TestMethod]
    public void Parallel_calls_of_one_scope_and_layered_protocols_are_not_duplicates()
    {
        var grpc = Span(Client, Root, SpanKind.Client, "orders.Orders/Create");
        var http = Span("0000000000000004", Client, SpanKind.Client, "POST");
        var first = Span("0000000000000005", Root, SpanKind.Client);
        var second = Span("0000000000000006", Root, SpanKind.Client);
        Assert.IsEmpty(Violations(new NoDuplicateSpansAttribute(), grpc, http, first, second));
    }

    [TestMethod]
    public void Capture_selection_reports_lost_unselected_and_invalid_fields()
    {
        var server = Span(Server, Root, attributes: new Dictionary<string, object?>
        {
            ["http.request.header.authorization"] = new object?[] { "secret" },
            ["url.path"] = "/orders"
        });
        Assert.AreSequenceEqual(
        [
            "[CapturesExactly] 'url.path' is not an HTTP header or RPC metadata attribute.",
            "[CapturesExactly] 'http.request.header.x-request-id' was selected, but no span captured it.",
            "[CapturesExactly] 'GET /orders' (Server, orders, Microsoft.AspNetCore) captured unselected "
            + "'http.request.header.authorization'."
        ], Violations(new CapturesExactlyAttribute(Selected, "url.path"), server));
    }

    [TestMethod]
    public void Capture_selection_accepts_the_exact_selection_and_an_empty_one_without_fields()
    {
        var selected = Span(Server, Root, attributes: new Dictionary<string, object?> { [Selected] = new object?[] { "request-1" } });
        Assert.IsEmpty(Violations(new CapturesExactlyAttribute(Selected), selected));
        Assert.IsEmpty(Violations(new CapturesExactlyAttribute(), Span(Server, Root)));
    }

    [TestMethod]
    public void Semantic_conventions_locate_type_violations_and_deprecations_unless_deprecations_are_allowed()
    {
        var server = Span(Server, Root, attributes: new Dictionary<string, object?>
        {
            ["http.response.status_code"] = "200",
            ["db.system"] = "postgresql",
            ["orders.custom"] = 7L
        });
        Assert.AreSequenceEqual(
        [
            "[ConformsToSemanticConventions] spans[0] 'GET /orders' (Server, orders, Microsoft.AspNetCore) "
            + "'db.system': The pinned registry marks this attribute deprecated.",
            "[ConformsToSemanticConventions] spans[0] 'GET /orders' (Server, orders, Microsoft.AspNetCore) "
            + "'http.response.status_code': Expected Integer."
        ], Violations(new ConformsToSemanticConventionsAttribute(), server).Order(StringComparer.Ordinal));
        Assert.AreSequenceEqual(
        [
            "[ConformsToSemanticConventions] spans[0] 'GET /orders' (Server, orders, Microsoft.AspNetCore) "
            + "'http.response.status_code': Expected Integer."
        ], Violations(new ConformsToSemanticConventionsAttribute { AllowDeprecated = true }, server));
    }

    [TestMethod]
    public void Error_telemetry_covers_trace_spans_and_every_error_log_but_not_fixture_spans_of_other_traces()
    {
        var failed = new SpanRecord(Trace, Server, "GET /orders")
        {
            ParentSpanId = Root,
            Kind = SpanKind.Server,
            Status = ActivityStatusCode.Error,
            StatusDescription = "boom",
            Scope = new InstrumentationScopeRecord { Name = "Microsoft.AspNetCore" },
            Resource = new ResourceRecord(new Dictionary<string, object?> { ["service.name"] = "orders" }),
            Events =
            [
                new SpanEventRecord("exception", 5, new Dictionary<string, object?>
                {
                    ["exception.type"] = "System.InvalidOperationException",
                    ["exception.message"] = "boom"
                })
            ]
        };
        var healthProbe = new SpanRecord("ffeeddccbbaa99887766554433221100", Client, "GET")
        {
            Kind = SpanKind.Client,
            Status = ActivityStatusCode.Error
        };
        LogRecord[] logs = [Log(17, "Microsoft.AspNetCore.Server.Kestrel", null), Log(13, "Orders", Trace)];
        var capture = new TelemetrySnapshot([failed, healthProbe], logs, [], []);
        Assert.AreSequenceEqual(
        [
            "[NoErrorTelemetry] 'GET /orders' (Server, orders, Microsoft.AspNetCore) ended with error status: boom",
            "[NoErrorTelemetry] 'GET /orders' (Server, orders, Microsoft.AspNetCore) recorded "
            + "\"System.InvalidOperationException\": \"boom\"",
            "[NoErrorTelemetry] orders logged Error from Microsoft.AspNetCore.Server.Kestrel: ConnectionError"
        ], Violations(new NoErrorTelemetryAttribute(), new PublishedTrace(new TraceEvidence(Trace, capture), Root)));
        Assert.IsEmpty(Violations(new NoErrorTelemetryAttribute(), Span(Server, Root)));
    }

    [TestMethod]
    public void Clean_exports_reject_rejections_errors_and_receiver_warnings()
    {
        ExportReceipt[] receipts =
        [
            new("logs", 1, 0, null) { Warnings = ["Coalesced identical OTLP attribute: ConnectionId."] },
            new("traces", 1, 2, "invalid span id") { Transport = OtlpTransport.Grpc },
            new("metrics", 3, 0, null)
        ];
        var capture = new TelemetrySnapshot([Span(Server, Root)], [], [], receipts);
        Assert.AreSequenceEqual(
        [
            "[ExportsCleanly] A HttpProtobuf logs export: Coalesced identical OTLP attribute: ConnectionId.",
            "[ExportsCleanly] A Grpc traces export rejected 2 record(s): invalid span id"
        ], Violations(new ExportsCleanlyAttribute(), new PublishedTrace(new TraceEvidence(Trace, capture), Root)));
        Assert.AreSequenceEqual(["[ExportsCleanly] The capture recorded no export."],
            Violations(new ExportsCleanlyAttribute(), Span(Server, Root)));
    }

    [TestMethod]
    public void Collector_preservation_accepts_identical_storage_and_withholds_policy_denied_keys()
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
    public void Collector_preservation_names_missing_spans_changed_values_and_policy_breaches()
    {
        var captured = Span(Server, Root, attributes: new Dictionary<string, object?>
        {
            ["http.response.status_code"] = 200L,
            ["url.path"] = "/orders",
            ["session.id"] = "lifted into a column"
        });
        var stored = Stored(captured, "GET", [],
            Attribute("http.response.status_code", new AttributeValue.StringValue("200")),
            Attribute("service.name", new AttributeValue.StringValue("orders")),
            Attribute("session.id", new AttributeValue.StringValue("lifted into a column")),
            Attribute("qyl.added", new AttributeValue.BoolValue(true)));
        var lost = Span(Client, Server, SpanKind.Client);
        Assert.AreSequenceEqual(
        [
            "[PreservedByCollector] 'GET /orders' (Server, orders, Microsoft.AspNetCore) was stored as 'GET'.",
            "[PreservedByCollector] 'GET /orders' (Server, orders, Microsoft.AspNetCore) changed "
            + "'http.response.status_code' from 200 to \"200\".",
            "[PreservedByCollector] 'GET /orders' (Server, orders, Microsoft.AspNetCore) retained 'session.id', "
            + "which the persistence policy denies.",
            "[PreservedByCollector] 'GET /orders' (Server, orders, Microsoft.AspNetCore) lost 'url.path' = \"/orders\".",
            "[PreservedByCollector] 'GET /orders' (Server, orders, Microsoft.AspNetCore) gained 'qyl.added' = true.",
            "[PreservedByCollector] 'GET /orders' (Server, orders, Microsoft.AspNetCore) resource lost 'service.name' = \"orders\".",
            "[PreservedByCollector] 'GET /orders' (Client, orders, Microsoft.AspNetCore) span 0000000000000003 was "
            + "captured but not stored."
        ], Violations(new PreservedByCollectorAttribute(), Published(captured, lost) with { Stored = [stored] }));
        Assert.AreSequenceEqual(
        [
            "[PreservedByCollector] Trace 00112233445566778899aabbccddeeff was not read back from a Collector. "
            + "Capture it through a TelemetryPipeline root; TelemetryCapture.TraceAsync has no Collector."
        ], Violations(new PreservedByCollectorAttribute(), captured));
    }

    [TestMethod]
    public void A_violation_fails_each_passing_result_with_the_trace_outline_and_an_evidence_file()
    {
        using var invocation = TelemetryInvocation.Begin([new ConnectedTraceAttribute()]);
        TelemetryInvocation.Publish(Published(Span(Server, Missing)));
        var passed = new TestResult { Outcome = UnitTestOutcome.Passed };
        var failed = new TestResult { Outcome = UnitTestOutcome.Failed };
        invocation.Verify([passed, failed]);

        Assert.AreEqual(UnitTestOutcome.Failed, passed.Outcome);
        var failure = Assert.IsInstanceOfType<AssertFailedException>(passed.TestFailureException);
        Assert.StartsWith("Telemetry policy violated (1):" + Environment.NewLine + "- [ConnectedTrace] 'GET /orders'", failure.Message);
        Assert.Contains("GET /orders  Server  orders  Microsoft.AspNetCore  span 0000000000000002  "
            + "(parent 00000000000000ff was not captured)", failure.Message);
        var evidence = Assert.ContainsSingle(passed.ResultFiles!);
        Assert.EndsWith("Evidence: " + evidence, failure.Message);
        Assert.Contains("names parent 00000000000000ff", File.ReadAllText(evidence));
        Assert.IsNull(failed.TestFailureException);
        Assert.IsNull(failed.ResultFiles);
    }

    [TestMethod]
    public void Satisfied_policies_leave_every_result_unchanged()
    {
        using var invocation = TelemetryInvocation.Begin([new ConnectedTraceAttribute()]);
        TelemetryInvocation.Publish(Published(Span(Server, Root)));
        var passed = new TestResult { Outcome = UnitTestOutcome.Passed };
        invocation.Verify([passed]);
        Assert.AreEqual(UnitTestOutcome.Passed, passed.Outcome);
        Assert.IsNull(passed.TestFailureException);
    }

    [TestMethod]
    public void Method_policies_replace_class_policies_of_the_same_type_and_keep_the_others()
    {
        var policies = TelemetryPolicyAttribute.DeclaredOn(typeof(DeclaringCases).GetMethod(nameof(DeclaringCases.Overrides))!);
        Assert.AreSequenceEqual(["CapturesExactly", "ConnectedTrace", "NoErrorTelemetry"],
            policies.Select(policy => policy.Name).Order(StringComparer.Ordinal));
        Assert.AreSequenceEqual(["http.request.header.b"], Assert.ContainsSingle(policies.OfType<CapturesExactlyAttribute>()).Fields);
    }

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
    public void Publishing_evidence_from_a_plain_test_class_with_policies_fails()
    {
        var trace = new PublishedTrace(new TraceEvidence("00112233445566778899aabbccddeeff", new TelemetrySnapshot([], [], [], [])), "0000000000000001");
        var error = Assert.ThrowsExactly<InvalidOperationException>(() => TelemetryInvocation.Publish(trace));
        Assert.AreEqual("Qyl.Telemetry.UnitTests.Policies.UnverifiedPolicyTests declares [ConnectedTrace], but only a "
            + "[TracePerTest] class verifies telemetry policies. Replace [TestClass] with [TracePerTest].", error.Message);
    }
}
