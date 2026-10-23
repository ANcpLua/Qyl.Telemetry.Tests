using Microsoft.Extensions.Time.Testing;

namespace Qyl.Telemetry.UnitTests.Receiver;

/// <summary>Drives the OTLP receiver's evidence deadline on a fake clock.</summary>
/// <param name="testContext">The runner context supplies cancellation.</param>
[TestClass]
[TestCategory("Fast")]
public class OtlpReceiverDeadlineTests(TestContext testContext)
{
    [TestMethod]
    public async Task Should_ThrowTimeoutException_When_OtlpReceiverAwait_EvidenceMissingAtTheDeadline()
    {
        var clock = new FakeTimeProvider();
        await using var receiver = await OtlpReceiver.StartAsync(OtlpReceiver.DefaultMaxPayloadBytes, clock, testContext.CancellationToken);
        var waiting = receiver.AwaitAsync(_ => false, OtlpReceiver.DefaultEvidenceTimeout, testContext.CancellationToken);
        clock.Advance(OtlpReceiver.DefaultEvidenceTimeout);
        _ = await Assert.ThrowsExactlyAsync<TimeoutException>(() => waiting);
    }

    [TestMethod]
    public async Task Should_KeepWaiting_When_OtlpReceiverAwait_EvidenceMissingJustBeforeTheDeadline()
    {
        var clock = new FakeTimeProvider();
        await using var receiver = await OtlpReceiver.StartAsync(OtlpReceiver.DefaultMaxPayloadBytes, clock, testContext.CancellationToken);
        var waiting = receiver.AwaitAsync(_ => false, OtlpReceiver.DefaultEvidenceTimeout, testContext.CancellationToken);
        clock.Advance(OtlpReceiver.DefaultEvidenceTimeout - TimeSpan.FromTicks(1));
        Assert.IsFalse(waiting.IsCompleted);
    }
}
