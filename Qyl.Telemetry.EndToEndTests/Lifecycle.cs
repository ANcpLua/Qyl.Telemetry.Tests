using Qyl.Telemetry.Tests.Architecture;

namespace Qyl.Telemetry.EndToEndTests;

[TestClass]
public static class Lifecycle
{
    [AssemblyInitialize]
    public static void Initialize(TestContext context) => ArchitectureFixtures.Initialize(context);

    [AssemblyCleanup]
    public static Task Cleanup(TestContext context) => ArchitectureFixtures.StopAsync(context);
}
