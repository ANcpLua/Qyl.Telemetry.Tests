using Qyl.Telemetry.SemanticConventions;

namespace Qyl.Telemetry.UnitTests.Receiver;

[TestClass]
public sealed class SpanAssertionTests
{
    private const string Trace = "00112233445566778899aabbccddeeff";
    private const string Parent = "0011223344556677";
    private const string Child = "8899aabbccddeeff";
    private const string Source = "native.source";
    private const string Operation = "request";

    [TestMethod]
    [DataRow(OwnershipMismatch.Source)]
    [DataRow(OwnershipMismatch.Operation)]
    [DataRow(OwnershipMismatch.Kind)]
    [DataRow(OwnershipMismatch.Duplicate)]
    public void SpanAssertions_SubstitutedOrDuplicateInstrumentation_FailsNativeOwnership(OwnershipMismatch mismatch)
    {
        var span = NativeSpan();
        Assert.AreSame(span, Assert.HasSingleNativeSpan([span], Source, Operation, SpanKind.Server));
        IEnumerable<SpanRecord> observed = mismatch == OwnershipMismatch.Duplicate ? [span, span] : [span];
        Assert.ThrowsExactly<AssertFailedException>(() => Assert.HasSingleNativeSpan(observed,
            mismatch == OwnershipMismatch.Source ? "replacement.source" : Source,
            mismatch == OwnershipMismatch.Operation ? "replacement" : Operation,
            mismatch == OwnershipMismatch.Kind ? SpanKind.Internal : SpanKind.Server));
    }

    [TestMethod]
    [DataRow(ContextMismatch.Trace)]
    [DataRow(ContextMismatch.Parent)]
    [DataRow(ContextMismatch.ReusedIdentity)]
    public void SpanAssertions_DisconnectedTraceWrongParentOrReusedIdentity_FailsParentCheck(ContextMismatch mismatch)
    {
        var parent = new SpanRecord(Trace, Parent, "parent");
        var span = NativeSpan();
        Assert.HasParentContext(span, parent);
        var changed = new SpanRecord(mismatch == ContextMismatch.Trace ? "ffeeddccbbaa99887766554433221100" : Trace,
            mismatch == ContextMismatch.ReusedIdentity ? Parent : Child, Operation)
        {
            ParentSpanId = mismatch == ContextMismatch.Parent ? Child : Parent
        };
        Assert.ThrowsExactly<AssertFailedException>(() => Assert.HasParentContext(changed, parent));
    }

    private static SpanRecord NativeSpan() => new(Trace, Child, Operation)
    {
        ParentSpanId = Parent,
        Kind = SpanKind.Server,
        Scope = new InstrumentationScopeRecord { Name = Source }
    };

    public enum OwnershipMismatch { Source, Operation, Kind, Duplicate }
    public enum ContextMismatch { Trace, Parent, ReusedIdentity }
}
