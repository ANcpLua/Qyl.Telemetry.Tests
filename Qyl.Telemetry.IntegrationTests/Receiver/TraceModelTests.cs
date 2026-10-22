using Google.Protobuf;
using JetBrains.Annotations;
using OpenTelemetry.Proto.Common.V1;
using Qyl.Telemetry.SemanticConventions;
using static Qyl.Telemetry.Tests.Evidence.OtlpEvidence;
using ProtoSpan = OpenTelemetry.Proto.Trace.V1.Span;

namespace Qyl.Telemetry.IntegrationTests.Receiver;

[TestClass]
public sealed class TraceModelTests
{
    [UsedImplicitly] public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task TraceModel_ActualParentChain_DerivesServiceCallsAndStoreIdentity()
    {
        await using var receiver = await OtlpReceiver.StartAsync(TestContext.CancellationToken);
        var request = RichSpan();
        var orders = request.ResourceSpans[0];
        orders.Resource.Attributes[0].Value.StringValue = "orders";
        var client = orders.ScopeSpans[0].Spans[0];
        client.Attributes.Add(Pair("session.id", new AnyValue { StringValue = "business-session" }));
        client.Kind = ProtoSpan.Types.SpanKind.Client;
        var payments = orders.Clone();
        payments.Resource.Attributes[0].Value.StringValue = "payments";
        var server = payments.ScopeSpans[0].Spans[0];
        server.Kind = ProtoSpan.Types.SpanKind.Server;
        server.ParentSpanId = Id("2222222222222222");
        server.SpanId = Id("3333333333333333");
        var database = server.Clone();
        database.Kind = ProtoSpan.Types.SpanKind.Client;
        database.ParentSpanId = Id("3333333333333333");
        database.SpanId = Id("4444444444444444");
        database.Name = "database.write";
        database.Attributes.Add(Pair("db.system.name", new AnyValue { StringValue = "postgresql" }));
        database.Attributes.Add(Pair("server.address", new AnyValue { StringValue = "db.internal" }));
        database.Attributes.Add(Pair("server.port", new AnyValue { IntValue = 5432 }));
        database.Attributes.Add(Pair("db.namespace", new AnyValue { StringValue = "payments" }));
        payments.ScopeSpans[0].Spans.Add(database);
        request.ResourceSpans.Add(payments);
        var unrelated = orders.Clone();
        unrelated.ScopeSpans[0].Spans[0].TraceId = Id("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        request.ResourceSpans.Add(unrelated);
        using var response =
            await ReceiverTests.PostAsync(receiver.Endpoint, request.ToByteArray(), TestContext.CancellationToken);
        response.EnsureSuccessStatusCode();
        var trace = new TraceEvidence("11111111111111111111111111111111", receiver.Snapshot());
        Assert.AreSequenceEqual(["orders", "payments"], trace.Services);
        Assert.AreEqual("business-session", trace.SessionId);
        Assert.AreSequenceEqual(["business-session"], trace.SessionIds);
        Assert.HasCount(3, trace.Spans);
        Assert.HasCount(4, trace.Capture.Spans);
        var call = Assert.ContainsSingle(trace.Calls);
        Assert.AreEqual("orders", call.From);
        Assert.AreEqual("payments", call.To);
        Assert.AreEqual("2222222222222222", call.Client.SpanId);
        Assert.AreEqual("3333333333333333", call.Server.SpanId);
        Assert.AreEqual(trace.TraceId, call.Client.TraceId);
        Assert.AreEqual(trace.TraceId, call.Server.TraceId);
        Assert.AreEqual(new StoreIdentity("postgresql", "db.internal", 5432, "payments"),
            Assert.ContainsSingle(trace.StoresOf("payments")));
        Assert.IsEmpty(trace.StoresOf("orders"));
    }

    [TestMethod]
    public async Task TraceModel_RepeatedExports_KeepRawObservationsWithoutInventingCalls()
    {
        await using var receiver = await OtlpReceiver.StartAsync(TestContext.CancellationToken);
        var request = RichSpan();
        var orders = request.ResourceSpans[0];
        orders.Resource.Attributes[0].Value.StringValue = "orders";
        var client = orders.ScopeSpans[0].Spans[0];
        client.Kind = ProtoSpan.Types.SpanKind.Client;
        var payments = orders.Clone();
        payments.Resource.Attributes[0].Value.StringValue = "payments";
        var server = payments.ScopeSpans[0].Spans[0];
        server.Kind = ProtoSpan.Types.SpanKind.Server;
        server.SpanId = Id("3333333333333333");
        server.ParentSpanId = client.SpanId;
        var nextClient = client.Clone();
        nextClient.SpanId = Id("5555555555555555");
        orders.ScopeSpans[0].Spans.Add(nextClient);
        var nextServer = server.Clone();
        nextServer.SpanId = Id("6666666666666666");
        nextServer.ParentSpanId = nextClient.SpanId;
        payments.ScopeSpans[0].Spans.Add(nextServer);
        request.ResourceSpans.Add(payments);
        var bytes = request.ToByteArray();
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var response = await ReceiverTests.PostAsync(receiver.Endpoint, bytes, TestContext.CancellationToken);
            response.EnsureSuccessStatusCode();
        }
        var capture = receiver.Snapshot();
        var trace = new TraceEvidence("11111111111111111111111111111111", capture);
        Assert.HasCount(8, capture.Spans);
        Assert.HasCount(8, trace.Spans);
        Assert.HasCount(2, capture.Requests);
        Assert.IsTrue(capture.Requests.All(export => export.Payload.Span.SequenceEqual(bytes)));
        Assert.HasCount(2, trace.Calls);
        Assert.AreSequenceEqual(["3333333333333333", "6666666666666666"],
            trace.Calls.Select(call => call.Server.SpanId).Order(StringComparer.Ordinal));
        Assert.IsTrue(trace.Calls.All(call => call is { From: "orders", To: "payments" }));
    }

}
