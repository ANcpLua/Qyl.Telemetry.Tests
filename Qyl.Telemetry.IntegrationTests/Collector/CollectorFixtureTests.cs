using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json;
using Qyl.Telemetry.Tests.Collector;

namespace Qyl.Telemetry.IntegrationTests.Collector;

/// <summary>Checks ownership and isolation of the actual packaged qyl collector process.</summary>
/// <param name="testContext">The runner owns cancellation.</param>
[TestClass]
[DoNotParallelize]
public sealed class CollectorFixtureTests(TestContext testContext) : IAsyncDisposable
{
    private CollectorFixture? _ownedCollector;
    private const string GracefulSignal = "TERM";
    private const string CrashSignal = "KILL";

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    [ResourceLock(WellKnownResources.EnvironmentVariables)]
    public async Task CollectorFixture_AmbientQylAndOtlpSettings_KeepsItsOwnEndpointAndDisposesTwice()
    {
        using var settings = new EnvironmentScope(("QYL_PORT", "invalid"), ("OTEL_EXPORTER_OTLP_ENDPOINT", "invalid"));
        var collector = await CollectorFixture.StartAsync(CollectorFixture.DefaultAssembly, cancellationToken: testContext.CancellationToken);
        await using (collector)
        {
            using var health = await TelemetryLifecycle.Http.GetAsync(new Uri(collector.Address, "health"), testContext.CancellationToken);
            Assert.AreEqual(HttpStatusCode.OK, health.StatusCode);
            Assert.IsEmpty(await collector.Client.LogsAsync("00000000000000000000000000000001", testContext.CancellationToken));
        }

        await collector.DisposeAsync();
        _ = await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() =>
            collector.Client.LogsAsync("trace", testContext.CancellationToken));
    }

    [TestMethod]
    [OSCondition(ConditionMode.Exclude, OperatingSystems.Windows)]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task CollectorFixture_SignalledOnUnix_ExitsGracefully()
    {
        var collector = await CollectorFixture.StartAsync(CollectorFixture.DefaultAssembly, cancellationToken: testContext.CancellationToken);
        await collector.DisposeAsync();
        using var shutdown = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(collector.DataDirectory, CollectorFixture.ShutdownFile), testContext.CancellationToken));
        Assert.AreEqual(0, shutdown.RootElement.GetProperty("ExitCode").GetInt32());
        Assert.IsFalse(shutdown.RootElement.GetProperty("Forced").GetBoolean());
    }

    [TestMethod]
    [OSCondition(ConditionMode.Exclude, OperatingSystems.Windows)]
    [Timeout(60_000, CooperativeCancellation = true)]
    [DataRow(GracefulSignal, 0)]
    [DataRow(CrashSignal, 137)]
    public async Task CollectorFixture_ExitsAfterReadinessBeforeDisposal_ReportsUnexpectedExitAndRetainsDiagnostics(
        string signal, int expectedExitCode)
    {
        var collector = _ownedCollector = await CollectorFixture.StartAsync(CollectorFixture.DefaultAssembly,
            testContext.TestTempDirectory, cancellationToken: testContext.CancellationToken);
        var signalResult = await SignalAsync(collector.ProcessId, signal, testContext.CancellationToken);
        Assert.AreEqual(0, signalResult.ExitCode);
        await collector.WaitForExitAsync(testContext.CancellationToken);

        var failure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => collector.DisposeAsync().AsTask());
        _ownedCollector = null;

        var log = Path.Combine(collector.DataDirectory, CollectorFixture.LogFile);
        Assert.AreEqual($"The Collector exited unexpectedly after becoming ready with code {expectedExitCode}. See {log}.", failure.Message);
        Assert.IsTrue(File.Exists(log));
        using var shutdown = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(collector.DataDirectory, CollectorFixture.ShutdownFile), testContext.CancellationToken));
        Assert.AreEqual(expectedExitCode, shutdown.RootElement.GetProperty("ExitCode").GetInt32());
        Assert.IsFalse(shutdown.RootElement.GetProperty("Forced").GetBoolean());
        testContext.AddResultFile(log);
    }

    public ValueTask DisposeAsync() => _ownedCollector is { } collector ? collector.DisposeAsync() : ValueTask.CompletedTask;

    [TestMethod]
    public async Task CollectorFixture_MissingArtifact_ThrowsBeforeCreatingAWorkspace()
    {
        var assembly = Path.Combine(AppContext.BaseDirectory, "missing-qyl-collector.dll");
        var workspace = Path.Combine(testContext.TestTempDirectory!, "not-created");
        var failure = await Assert.ThrowsExactlyAsync<FileNotFoundException>(() =>
            CollectorFixture.StartAsync(assembly, workspace, cancellationToken: testContext.CancellationToken));
        Assert.AreEqual(assembly, failure.FileName);
        Assert.IsFalse(Directory.Exists(workspace));
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task CollectorFixture_InvalidAssembly_ReportsItsEarlyExitAndIsReaped()
    {
        var assembly = Path.Combine(testContext.TestTempDirectory!, "invalid-qyl-collector.dll");
        await File.WriteAllTextAsync(assembly, "not a managed executable", testContext.CancellationToken);
        var failure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            CollectorFixture.StartAsync(assembly, testContext.TestTempDirectory, cancellationToken: testContext.CancellationToken));
        Assert.Contains("qyl exited with code", failure.Message);
        var workspace = Assert.ContainsSingle(Directory.GetDirectories(testContext.TestTempDirectory!, "qyl-collector-*"));
        using var shutdown = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(workspace, CollectorFixture.ShutdownFile), testContext.CancellationToken));
        Assert.AreNotEqual(0, shutdown.RootElement.GetProperty("ExitCode").GetInt32());
        Assert.IsFalse(shutdown.RootElement.GetProperty("Forced").GetBoolean());
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task CollectorFixture_StartupCancelled_TerminatesAndJoinsTheProcess()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(testContext.CancellationToken);
        await cancellation.CancelAsync();
        _ = await Assert.ThrowsAsync<OperationCanceledException>(() => CollectorFixture.StartAsync(CollectorFixture.DefaultAssembly, cancellationToken: cancellation.Token));
    }

    private static Task<(int ExitCode, string Output, string Error)> SignalAsync(int processId, string signal,
        CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo("/bin/kill")
        {
            ArgumentList = { "-" + signal, processId.ToString(CultureInfo.InvariantCulture) }
        };
        return ChildProcess.RunAsync(start, "the external Collector exit signal", cancellationToken);
    }
}
