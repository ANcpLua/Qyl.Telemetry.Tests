using Qyl.Telemetry.Tests.Architecture;
using JetBrains.Annotations;

namespace Qyl.Telemetry.IntegrationTests;

[TestClass]
public static class TelemetryLifecycle
{
    internal static readonly HttpClient Http = new();

    [AssemblyInitialize]
    public static void StartAssembly(TestContext context) => ArchitectureFixtures.Initialize(context);

    [AssemblyCleanup]
    public static async Task CloseAssembly(TestContext context)
    {
        try { await ArchitectureFixtures.StopAsync(context); }
        finally { Http.Dispose(); }
    }

    [UsedImplicitly]
    [GlobalTestInitialize]
    public static void Initialize(TestContext context)
    {
        QylTestHooks.Initialize(context);
    }

    [UsedImplicitly]
    [GlobalTestCleanup]
    public static void Cleanup(TestContext context)
    {
        QylTestHooks.Cleanup(context);
    }
}
