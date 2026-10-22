using JetBrains.Annotations;

namespace Qyl.Telemetry.Tests.Policies;

/// <summary>
///     Every exported span of the test's traces descends from the test root: each parent is a captured span of the
///     same trace or the root itself. Detects lost propagation and spans parented to unexported activities.
/// </summary>
[PublicAPI]
public sealed class ConnectedTraceAttribute : TelemetryPolicyAttribute
{
    private protected override IEnumerable<string> Violations(TelemetryInvocation invocation)
    {
        foreach (var trace in invocation.Traces)
        {
            var spans = trace.Evidence.Spans.DistinctBy(span => span.SpanId).ToArray();
            if (spans.Length == 0)
            {
                yield return $"Trace {trace.Evidence.TraceId} exported no span.";
                continue;
            }

            var known = spans.Select(span => span.SpanId).Append(trace.RootSpanId).ToHashSet(StringComparer.Ordinal);
            foreach (var span in spans)
            {
                if (TelemetryId.IsAbsent(span.ParentSpanId))
                {
                    if (span.SpanId != trace.RootSpanId)
                        yield return $"{Describe(span)} starts a second root in trace {trace.Evidence.TraceId}.";
                }
                else if (!known.Contains(span.ParentSpanId))
                {
                    yield return $"{Describe(span)} names parent {span.ParentSpanId}, which is neither a captured span "
                        + "nor the test root.";
                }
            }
        }
    }
}
