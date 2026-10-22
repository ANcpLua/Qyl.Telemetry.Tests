using System.Diagnostics;
using Qyl.Telemetry.SemanticConventions;

namespace Qyl.Telemetry.Tests;

/// <summary>Ownership and context checks for spans decoded from the actual OTLP stream.</summary>
[JetBrains.Annotations.PublicAPI]
public static class SpanAssertions
{
    extension(Assert)
    {
        public static SpanRecord HasSingleNativeSpan(IEnumerable<SpanRecord> spans,
            string source, string operation, SpanKind kind)
        {
            var span = Assert.ContainsSingle(spans, $"Expected one native '{source}' span for '{operation}'.");
            Assert.AreEqual(source, span.Scope?.Name, "The native instrumentation source changed.");
            Assert.AreEqual(operation, span.Name, "The native operation changed.");
            Assert.AreEqual(kind, span.Kind, "The native span kind changed.");
            return span;
        }

        public static void HasParentContext(SpanRecord child, SpanRecord parent) =>
            RequireParent(child, parent.TraceId, parent.SpanId);

        public static void HasParentContext(SpanRecord child, ActivityContext parent) =>
            RequireParent(child, parent.TraceId.ToHexString(), parent.SpanId.ToHexString());
    }

    private static void RequireParent(SpanRecord child, string trace, string parent)
    {
        RequireIdentity(trace, parent);
        RequireIdentity(child.TraceId, child.SpanId);
        Assert.AreEqual(trace, child.TraceId, $"'{child.Name}' did not preserve its parent's trace.");
        Assert.AreEqual(parent, child.ParentSpanId, $"'{child.Name}' did not preserve its parent span.");
        Assert.AreNotEqual(parent, child.SpanId, $"'{child.Name}' reused its parent's span identity.");
    }

    private static void RequireIdentity(string trace, string span)
    {
        Assert.IsTrue(TelemetryId.IsValid(trace, 32),
            $"Expected a nonzero trace ID, observed '{trace}'.");
        Assert.IsTrue(TelemetryId.IsValid(span, 16),
            $"Expected a nonzero span ID, observed '{span}'.");
    }
}
