namespace Qyl.Telemetry.EndToEndTests;

[JetBrains.Annotations.UsedImplicitly(JetBrains.Annotations.ImplicitUseTargetFlags.WithMembers)]
internal static class ProbeSignals
{
    internal const string MeterName = "Fixture.TelemetryProbe";
    internal const string CounterName = "fixture.telemetry.requests";
    internal const string CounterUnit = "{request}";
    internal const string CompletedEvent = "probe.completed";
    internal const string CompletedLog = "ProbeCompleted";
    internal const string RequestAttribute = "probe.request_id";
    internal const string RequestHeader = "X-Probe-Id";
    internal const string RequestId = "probe-request";
    internal const string FailureMessage = "probe operation failed";
}
