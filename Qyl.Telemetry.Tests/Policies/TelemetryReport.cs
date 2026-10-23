using System.Diagnostics;
using System.Globalization;
using System.Text;
using Qyl.Telemetry.SemanticConventions;

namespace Qyl.Telemetry.Tests.Policies;

/// <summary>Renders published traces as span trees for policy failures and their evidence file.</summary>
internal static class TelemetryReport
{
    private const int OutlineLines = 40;

    internal static string Outline(TelemetryInvocation invocation)
    {
        var lines = invocation.Traces.SelectMany(trace => Tree(trace, details: false)).ToArray();
        return string.Join(Environment.NewLine, lines.Length <= OutlineLines
            ? lines
            : lines.Take(OutlineLines).Append($"... {lines.Length - OutlineLines} more lines in the evidence file."));
    }

    internal static string Render(TelemetryInvocation invocation, IReadOnlyList<string> violations)
    {
        var text = new StringBuilder();
        text.AppendLine($"registry: {SchemaUrl.Qyl}, upstream: {SchemaUrl.Current}, sha256: {RegistrySnapshot.Sha256}");
        foreach (var violation in violations) text.AppendLine(violation);
        foreach (var trace in invocation.Traces)
        {
            text.AppendLine();
            foreach (var line in Tree(trace, details: true)) text.AppendLine(line);
            if (trace.Stored is not { } stored) continue;
            text.AppendLine($"  Collector stored {stored.Count} span(s):");
            foreach (var span in stored)
            {
                text.AppendLine($"    {span.Name}  {span.Kind}  {span.Resource.ServiceName}  "
                    + $"{span.InstrumentationScope?.ScopeName ?? "no scope"}  span {span.SpanId} parent {span.ParentSpanId}  {span.Status.Code}");
                foreach (var attribute in span.Attributes ?? [])
                    text.AppendLine($"      {attribute.Key} = {Format(PreservedByCollectorAttribute.Plain(attribute.Value))}");
                foreach (var attribute in span.Resource.Attributes ?? [])
                    text.AppendLine($"      resource {attribute.Key} = {Format(PreservedByCollectorAttribute.Plain(attribute.Value))}");
                foreach (var spanEvent in span.Events ?? [])
                    text.AppendLine($"      event {spanEvent.Name} at {spanEvent.TimeUnixNano}: "
                        + string.Join(", ", (spanEvent.Attributes ?? []).Select(attribute => $"{attribute.Key} = {Format(PreservedByCollectorAttribute.Plain(attribute.Value))}")));
            }
        }

        foreach (var capture in invocation.Captures)
        {
            text.AppendLine().AppendLine($"capture: {capture.Spans.Count} span(s), {capture.Logs.Count} log(s), "
                + $"{capture.Metrics.Count} metric(s), {capture.Receipts.Count} export(s)");
            foreach (var receipt in capture.Receipts)
                text.AppendLine($"  export {receipt.Transport} {receipt.Signal}: accepted {receipt.Accepted}, rejected {receipt.Rejected}"
                    + (receipt.Error is null ? "" : $", error {receipt.Error}")
                    + string.Concat(receipt.Warnings.Select(warning => $", warning {warning}")));
            foreach (var log in capture.Logs)
                text.AppendLine($"  log {log.SeverityText}({log.SeverityNumber}) {Service(log.Resource)} {log.Scope?.Name ?? "-"} "
                    + $"{(string.IsNullOrEmpty(log.EventName) ? Format(log.Body) : log.EventName)} trace {log.TraceId ?? "-"} span {log.SpanId ?? "-"}");
            foreach (var metric in capture.Metrics)
                text.AppendLine($"  metric {metric.Name} [{metric.Unit}] {Service(metric.Resource)} {metric.Scope?.Name ?? "-"}");
        }

        return text.ToString();
    }

    internal static string Service(ResourceRecord resource) =>
        resource.Attributes.TryGetValue("service.name", out var name) && name is string service ? service : "no service";

    internal static string Format(object? value) => value switch
    {
        null => "null",
        string text => $"\"{text}\"",
        bool flag => flag ? "true" : "false",
        byte[] bytes => "base64:" + Convert.ToBase64String(bytes),
        IReadOnlyDictionary<string, object?> map => "{" + string.Join(", ", map.Select(pair => $"{pair.Key}: {Format(pair.Value)}")) + "}",
        IReadOnlyList<object?> items => "[" + string.Join(", ", items.Select(Format)) + "]",
        IFormattable number => number.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? ""
    };

    private static List<string> Tree(PublishedTrace trace, bool details)
    {
        var spans = trace.Evidence.Spans.DistinctBy(span => span.SpanId).ToArray();
        var ids = spans.Select(span => span.SpanId).ToHashSet(StringComparer.Ordinal);
        var children = spans.ToLookup(span => span.ParentSpanId ?? "", StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var lines = new List<string> { $"trace {trace.Evidence.TraceId}, test root {trace.RootSpanId}, {spans.Length} span(s)" };
        foreach (var root in spans.Where(span => string.IsNullOrEmpty(span.ParentSpanId) || !ids.Contains(span.ParentSpanId))
                     .OrderBy(span => span.StartTimeUnixNano))
            Walk(root, 1);
        foreach (var unrooted in spans.Where(span => !visited.Contains(span.SpanId)).OrderBy(span => span.StartTimeUnixNano))
        {
            if (visited.Contains(unrooted.SpanId)) continue;
            lines.Add("  unrooted component (cyclic parentage):");
            Walk(unrooted, 2);
        }
        return lines;

        void Walk(SpanRecord span, int depth)
        {
            if (!visited.Add(span.SpanId)) return;
            var indent = new string(' ', depth * 2);
            var parent = string.IsNullOrEmpty(span.ParentSpanId) ? ""
                : span.ParentSpanId == trace.RootSpanId ? "  (under the test root)"
                : ids.Contains(span.ParentSpanId) ? "" : $"  (parent {span.ParentSpanId} was not captured)";
            lines.Add($"{indent}{span.Name}  {span.Kind}  {span.Service ?? "no service"}  {span.Scope?.Name ?? "no scope"}  "
                + $"span {span.SpanId}{(span.Status == ActivityStatusCode.Error ? "  ERROR " + span.StatusDescription : "")}{parent}");
            if (details)
            {
                lines.AddRange(span.Attributes.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .Select(pair => $"{indent}  {pair.Key} = {Format(pair.Value)}"));
                lines.AddRange(span.Events.Select(spanEvent => $"{indent}  event {spanEvent.Name} {Format(spanEvent.Attributes)}"));
            }

            foreach (var child in children[span.SpanId].OrderBy(child => child.StartTimeUnixNano)) Walk(child, depth + 1);
        }
    }
}
