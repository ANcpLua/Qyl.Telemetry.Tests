using System.Diagnostics;
using Qyl.Telemetry.SemanticConventions;

namespace Qyl.Telemetry.Tests.Collector;

/// <summary>One request to an instrumented application: the pipeline root it ran under and the trace captured for it.</summary>
[JetBrains.Annotations.PublicAPI]
public sealed record ApplicationRoundTrip(ActivityContext Root, TraceEvidence Evidence);

/// <summary>Where the instrumented application's server span of a round trip sits in its trace.</summary>
[JetBrains.Annotations.PublicAPI]
public static class ApplicationRoundTripAssertions
{
    /// <summary>
    ///     Requires the application's one native ASP.NET Core server span for <paramref name="operation" />, under the
    ///     test process's HTTP client span, under the round trip's root, and returns the server span.
    /// </summary>
    public static SpanRecord HasServerSpanUnderTheTestClient(this Assert _, ApplicationRoundTrip roundTrip, string operation)
    {
        ArgumentNullException.ThrowIfNull(roundTrip);
        var spans = roundTrip.Evidence.Spans;
        var server = SpanAssertions.HasSingleNativeSpan(spans.Where(span => span.Service == InstrumentedApplicationFixture.ServiceName),
            "Microsoft.AspNetCore", operation, SpanKind.Server);
        var client = Assert.ContainsSingle(spans.Where(span => span.SpanId == server.ParentSpanId),
            $"Expected one captured parent of '{operation}'.");
        Assert.AreEqual(SpanKind.Client, client.Kind, "The server span's parent is not an HTTP client span.");
        Assert.AreEqual(TelemetryPipeline.TestProcess, client.Service, "The HTTP client span is not the test process's.");
        SpanAssertions.HasParentContext(server, client);
        SpanAssertions.HasParentContext(client, roundTrip.Root);
        return server;
    }

}
