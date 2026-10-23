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
            var parents = spans.ToDictionary(span => span.SpanId, span => span.ParentSpanId, StringComparer.Ordinal);
            var cyclic = new Dictionary<string, bool>(StringComparer.Ordinal);
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
                else if (HasCyclicParentage(span.SpanId, parents, cyclic))
                    yield return $"{Describe(span)} has cyclic parentage in trace {trace.Evidence.TraceId}.";
            }
        }
    }

    private static bool HasCyclicParentage(string id, Dictionary<string, string?> parents,
        Dictionary<string, bool> known)
    {
        var path = new HashSet<string>(StringComparer.Ordinal);
        var cycle = false;
        while (!TelemetryId.IsAbsent(id) && parents.TryGetValue(id, out var parent))
        {
            if (known.TryGetValue(id, out cycle)) break;
            if (!path.Add(id)) { cycle = true; break; }
            id = parent ?? "";
        }
        foreach (var visited in path) known[visited] = cycle;
        return cycle;
    }
}
