namespace Qyl.Telemetry.IntegrationTests;

/// <summary>OTLP signal names that select the receiver route and the request shape.</summary>
internal static class Signals
{
    public const string Traces = "traces";
    public const string Metrics = "metrics";
    public const string Logs = "logs";
}
