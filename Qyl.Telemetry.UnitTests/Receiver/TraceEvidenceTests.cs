using Qyl.Telemetry.SemanticConventions;

namespace Qyl.Telemetry.UnitTests.Receiver;

/// <summary>Checks trace relationships against canonical records without changing captured evidence.</summary>
[TestClass]
public sealed class TraceEvidenceTests
{
    [TestMethod]
    public void TraceEvidence_SelectedTrace_KeepsTheOriginalCaptureInstance()
    {
        var (capture, trace, _) = SelectedTrace();
        Assert.AreSame(capture, trace.Capture);
    }

    [TestMethod]
    public void TraceEvidence_SelectedTrace_KeepsUnrelatedCapturedSpans()
    {
        var (_, trace, _) = SelectedTrace();
        Assert.HasCount(2, trace.Capture.Spans);
    }

    [TestMethod]
    public void TraceEvidence_SelectedTrace_SpansAreOnlyItsOwn()
    {
        var (_, trace, selected) = SelectedTrace();
        Assert.AreSame(selected, Assert.ContainsSingle(trace.Spans));
    }

    [TestMethod]
    public void TraceEvidenceCalls_CrossServiceClientServerChain_IsTheOnlyCall()
    {
        var call = Assert.ContainsSingle(MixedCallTrace().Calls);
        Assert.AreEqual(("orders", "payments"), (call.From, call.To));
    }

    [TestMethod]
    public void TraceEvidence_RepeatedExport_KeepsEverySpanRecord()
    {
        Assert.HasCount(8, MixedCallTrace().Spans);
    }

    [TestMethod]
    public void TraceEvidence_Services_AreDistinctAndOrdered()
    {
        Assert.AreSequenceEqual(["a", "test", "z"], StoreTrace().Services);
    }

    [TestMethod]
    public void TraceEvidenceStores_OfOneService_AreDistinctByNamespace()
    {
        Assert.AreSequenceEqual(["another", "first"], StoreTrace().StoresOf("a").Select(store => store.Namespace).Order(StringComparer.Ordinal));
    }

    [TestMethod]
    public void TraceEvidenceStores_OfAnUnknownService_AreEmpty()
    {
        Assert.IsEmpty(StoreTrace().StoresOf("missing"));
    }

    [TestMethod]
    public void TraceEvidenceStores_SameNamespaceOnDifferentServers_AreDistinct()
    {
        var trace = TestSpan.Trace(TestSpan.Create("orders", "1", store: "orders", server: "db-a"),
            TestSpan.Create("orders", "2", store: "orders", server: "db-b"));
        Assert.AreSequenceEqual(["db-a", "db-b"], trace.StoresOf("orders").Select(store => store.ServerAddress).Order(StringComparer.Ordinal));
    }

    private static (TelemetrySnapshot Capture, TraceEvidence Trace, SpanRecord Selected) SelectedTrace()
    {
        var selected = TestSpan.Create("orders", "selected");
        var unrelated = new SpanRecord(TestSpan.UnrelatedTraceId, "unrelated", "other operation");
        var capture = TestSpan.Only(selected, unrelated);
        return (capture, new TraceEvidence(selected.TraceId, capture), selected);
    }

    private static TraceEvidence MixedCallTrace()
    {
        var client = TestSpan.Create("orders", "client", SpanKind.Client);
        var server = TestSpan.Create("payments", "server", SpanKind.Server, "client");
        return TestSpan.Trace(client, server, server,
            TestSpan.Create("payments", "root", SpanKind.Server),
            TestSpan.Create("payments", "orphan", SpanKind.Server, "missing"),
            TestSpan.Create("orders", "same-service", SpanKind.Server, "client"),
            TestSpan.Create("payments", "internal", SpanKind.Internal, "client"),
            TestSpan.Create("payments", "wrong-parent", SpanKind.Server, "internal"));
    }

    private static TraceEvidence StoreTrace() => TestSpan.Trace(TestSpan.Create("z", "1", store: "second"),
        TestSpan.Create("a", "2", store: "first"), TestSpan.Create("a", "3", store: "first"),
        TestSpan.Create("a", "4", store: "another"), TestSpan.Create("test", "5"));
}
