using Microsoft.Extensions.Time.Testing;

namespace Qyl.Telemetry.UnitTests.Receiver;

/// <summary>Drives the OTLP receiver's evidence deadline on a fake clock.</summary>
/// <param name="testContext">The runner context supplies cancellation.</param>
[TestClass]
public sealed class OtlpReceiverDeadlineTests(TestContext testContext)
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    [TestMethod]
    public async Task OtlpReceiverAwait_EvidenceMissingAtTheDeadline_ThrowsTimeoutException()
    {
        var clock = new FakeTimeProvider();
        await using var receiver = await OtlpReceiver.StartAsync(OtlpReceiver.DefaultMaxPayloadBytes, clock, testContext.CancellationToken);
        var waiting = receiver.AwaitAsync(_ => false, Deadline, testContext.CancellationToken);
        clock.Advance(Deadline);
        _ = await Assert.ThrowsExactlyAsync<TimeoutException>(() => waiting);
    }

    [TestMethod]
    public async Task OtlpReceiverAwait_EvidenceMissingJustBeforeTheDeadline_KeepsWaiting()
    {
        var clock = new FakeTimeProvider();
        await using var receiver = await OtlpReceiver.StartAsync(OtlpReceiver.DefaultMaxPayloadBytes, clock, testContext.CancellationToken);
        var waiting = receiver.AwaitAsync(_ => false, Deadline, testContext.CancellationToken);
        clock.Advance(Deadline - TimeSpan.FromTicks(1));
        Assert.IsFalse(waiting.IsCompleted);
    }
}
