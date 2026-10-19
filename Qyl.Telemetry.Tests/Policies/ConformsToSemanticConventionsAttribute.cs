using System.Globalization;
using System.Text.RegularExpressions;
using JetBrains.Annotations;

namespace Qyl.Telemetry.Tests.Policies;

/// <summary>
///     Every captured registry attribute has its registered value type, every registered metric its unit, and no
///     deprecated attribute or metric appears unless <see cref="AllowDeprecated" /> is set. Covers the complete
///     captures of the test, including resources, scopes, events, links, logs and metric points. Application-defined
///     names stay valid.
/// </summary>
[PublicAPI]
public sealed partial class ConformsToSemanticConventionsAttribute : TelemetryPolicyAttribute
{
    public bool AllowDeprecated { get; set; }

    private protected override IEnumerable<string> Violations(TelemetryInvocation invocation)
    {
        foreach (var capture in invocation.Captures)
        {
            var report = RegistryConformance.Check(capture);
            if (report.RecordsChecked == 0) yield return "The capture contains no span, log or metric to check.";
            foreach (var issue in report.Issues.Where(issue => issue.Level == RegistryIssueLevel.Violation || !AllowDeprecated))
                yield return $"{Locate(capture, issue.Location)} '{issue.Name}': {issue.Message}";
        }
    }

    private static string Locate(TelemetrySnapshot capture, string location)
    {
        var match = RecordIndex().Match(location);
        if (!match.Success) return location;
        var index = int.Parse(match.Groups["index"].Value, CultureInfo.InvariantCulture);
        return match.Groups["signal"].Value switch
        {
            "spans" => $"{location} {Describe(capture.Spans[index])}",
            "logs" => $"{location} log of {TelemetryReport.Service(capture.Logs[index].Resource)}",
            _ => $"{location} metric '{capture.Metrics[index].Name}'"
        };
    }

    [GeneratedRegex(@"^(?<signal>spans|logs|metrics)\[(?<index>\d+)\]")]
    private static partial Regex RecordIndex();
}
