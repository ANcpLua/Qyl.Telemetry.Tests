namespace Qyl.Telemetry.IntegrationTests;

[TestClass]
public static class TelemetryLifecycle
{
    internal static readonly HttpClient Http = new();

    [AssemblyCleanup]
    public static void CloseAssembly() => Http.Dispose();
}
