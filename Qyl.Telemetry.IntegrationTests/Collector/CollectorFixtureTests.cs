using System.Net;
using System.Text.Json;
using Qyl.Telemetry.Tests.Collector;

namespace Qyl.Telemetry.IntegrationTests.Collector;

/// <summary>Checks ownership and isolation of the actual packaged qyl collector process.</summary>
/// <param name="testContext">The runner owns cancellation.</param>
[TestClass]
[DoNotParallelize]
public sealed class CollectorFixtureTests(TestContext testContext) : IDisposable
{
    private readonly HttpClient _client = new();
    private static string AssemblyPath => Path.Combine(AppContext.BaseDirectory, "qyl", "collector", "qyl.collector.dll");

    /// <summary>Ambient qyl and OTLP settings cannot redirect the isolated collector.</summary>
    [TestMethod]
    public async Task CollectorHasItsOwnEndpointAndCanBeDisposedTwice()
    {
        using var settings = new EnvironmentScope(("QYL_PORT", "invalid"), ("OTEL_EXPORTER_OTLP_ENDPOINT", "invalid"));
        var collector = await CollectorFixture.StartAsync(AssemblyPath, cancellationToken: testContext.CancellationToken);
        await using (collector)
        {
            using var health = await _client.GetAsync(new Uri(collector.Address, "health"), testContext.CancellationToken);
            Assert.AreEqual(HttpStatusCode.OK, health.StatusCode);
            Assert.IsEmpty(await collector.Client.LogsAsync("00000000000000000000000000000001", testContext.CancellationToken));
        }

        await collector.DisposeAsync();
        if (!OperatingSystem.IsWindows())
        {
            using var shutdown = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(collector.DataDirectory, "shutdown.json"), testContext.CancellationToken));
            Assert.AreEqual(0, shutdown.RootElement.GetProperty("ExitCode").GetInt32());
            Assert.IsFalse(shutdown.RootElement.GetProperty("Forced").GetBoolean());
        }
        _ = await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() =>
            collector.Client.LogsAsync("trace", testContext.CancellationToken));
    }

    /// <summary>A missing input is rejected before a process or workspace is acquired.</summary>
    [TestMethod]
    public async Task MissingCollectorArtifactDoesNotCreateAWorkspace()
    {
        var assembly = Path.Combine(AppContext.BaseDirectory, "missing-qyl-collector.dll");
        var workspace = Path.Combine(testContext.TestTempDirectory!, "not-created");
        var failure = await Assert.ThrowsExactlyAsync<FileNotFoundException>(() =>
            CollectorFixture.StartAsync(assembly, workspace, testContext.CancellationToken));
        Assert.AreEqual(assembly, failure.FileName);
        Assert.IsFalse(Directory.Exists(workspace));
    }

    /// <summary>An early process exit reports its original output and is reaped.</summary>
    [TestMethod]
    public async Task InvalidCollectorAssemblyReportsItsExit()
    {
        var assembly = Path.Combine(testContext.TestTempDirectory!, "invalid-qyl-collector.dll");
        await File.WriteAllTextAsync(assembly, "not a managed executable", testContext.CancellationToken);
        var failure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            CollectorFixture.StartAsync(assembly, testContext.TestTempDirectory, testContext.CancellationToken));
        Assert.Contains("qyl exited with code", failure.Message);
        var workspace = Assert.ContainsSingle(Directory.GetDirectories(testContext.TestTempDirectory!, "qyl-collector-*"));
        using var shutdown = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(workspace, "shutdown.json"), testContext.CancellationToken));
        Assert.AreNotEqual(0, shutdown.RootElement.GetProperty("ExitCode").GetInt32());
        Assert.IsFalse(shutdown.RootElement.GetProperty("Forced").GetBoolean());
    }

    /// <summary>Cancelling startup terminates and joins the process instead of leaking it.</summary>
    [TestMethod]
    public async Task CollectorStartupHonoursCancellation()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(testContext.CancellationToken);
        await cancellation.CancelAsync();
        _ = await Assert.ThrowsAsync<OperationCanceledException>(() => CollectorFixture.StartAsync(AssemblyPath, cancellationToken: cancellation.Token));
    }
    public void Dispose() => _client.Dispose();
}
