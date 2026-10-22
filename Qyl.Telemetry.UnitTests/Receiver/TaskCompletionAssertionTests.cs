using Microsoft.Extensions.Time.Testing;

namespace Qyl.Telemetry.UnitTests.Receiver;

[TestClass]
public sealed class TaskCompletionAssertionTests(TestContext testContext)
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task AllCompleteAsync_EmptyCalls_CompletesSuccessfully()
    {
        await Assert.AllCompleteAsync([], Deadline, new FakeTimeProvider(), testContext.CancellationToken);
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task AllCompleteAsync_CompletedCalls_CompletesSuccessfully()
    {
        await Assert.AllCompleteAsync([Task.CompletedTask, Task.FromResult(42)], Deadline,
            new FakeTimeProvider(), testContext.CancellationToken);
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task AllCompleteAsync_CallFinishesBeforeDeadline_CompletesSuccessfully()
    {
        var clock = new FakeTimeProvider();
        var call = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var assertion = Assert.AllCompleteAsync([call.Task], Deadline, clock, testContext.CancellationToken);
        call.SetResult();

        await assertion;
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task AllCompleteAsync_OneCallStillPending_KeepsWaitingForThatCall()
    {
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var assertion = Assert.AllCompleteAsync([first.Task, second.Task], Deadline,
            new FakeTimeProvider(), testContext.CancellationToken);
        first.SetResult();

        Assert.IsFalse(assertion.IsCompleted);

        second.SetResult();
        await assertion;
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task AllCompleteAsync_OneTickBeforeDeadline_KeepsWaiting()
    {
        var clock = new FakeTimeProvider();
        var call = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var assertion = Assert.AllCompleteAsync([call.Task], Deadline, clock, testContext.CancellationToken);
        clock.Advance(Deadline - TimeSpan.FromTicks(1));

        Assert.IsFalse(assertion.IsCompleted);

        call.SetResult();
        await assertion;
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task AllCompleteAsync_DeadlineExpires_ReportsOriginalIndicesAndStates()
    {
        var clock = new FakeTimeProvider();
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var assertion = Assert.AllCompleteAsync([Task.CompletedTask, first.Task, Task.CompletedTask, second.Task],
            Deadline, clock, testContext.CancellationToken);
        clock.Advance(Deadline);

        var failure = await Assert.ThrowsExactlyAsync<AssertFailedException>(() => assertion);

        Assert.AreEqual(string.Join(Environment.NewLine,
            "Not all calls completed within 00:00:30.",
            "Call states observed after the deadline:",
            "[0] RanToCompletion",
            "[1] WaitingForActivation",
            "[2] RanToCompletion",
            "[3] WaitingForActivation"), failure.Message);
        first.SetResult();
        second.SetResult();
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task AllCompleteAsync_DeadlineExpires_DoesNotCompleteTheCall()
    {
        var clock = new FakeTimeProvider();
        var call = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var assertion = Assert.AllCompleteAsync([call.Task], Deadline, clock, testContext.CancellationToken);
        clock.Advance(Deadline);
        await Assert.ThrowsExactlyAsync<AssertFailedException>(() => assertion);

        Assert.IsFalse(call.Task.IsCompleted);
        call.SetResult();
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task AllCompleteAsync_FaultedCall_PropagatesTheOriginalException()
    {
        var expected = new InvalidOperationException("call failed");

        var actual = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => Assert.AllCompleteAsync(
            [Task.FromException(expected)], Deadline, new FakeTimeProvider(), testContext.CancellationToken));

        Assert.AreSame(expected, actual);
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task AllCompleteAsync_CallThrowsTimeout_PropagatesTheOriginalException()
    {
        var expected = new TimeoutException("the call's own timeout");

        var actual = await Assert.ThrowsExactlyAsync<TimeoutException>(() => Assert.AllCompleteAsync(
            [Task.FromException(expected)], Deadline, new FakeTimeProvider(), testContext.CancellationToken));

        Assert.AreSame(expected, actual);
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task AllCompleteAsync_PendingCallThrowsTimeout_PropagatesTheOriginalException()
    {
        var expected = new TimeoutException("the call's own timeout");
        var call = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var assertion = Assert.AllCompleteAsync([call.Task], Deadline,
            new FakeTimeProvider(), testContext.CancellationToken);
        call.SetException(expected);

        var actual = await Assert.ThrowsExactlyAsync<TimeoutException>(() => assertion);

        Assert.AreSame(expected, actual);
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task AllCompleteAsync_CallFaultsAfterDeadline_StillReportsTheAssertionDeadline()
    {
        var clock = new FakeTimeProvider();
        var call = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var expected = new TimeoutException("the call's later timeout");
        var assertion = Assert.AllCompleteAsync([call.Task], Deadline, clock, testContext.CancellationToken);
        clock.Advance(Deadline);
        call.SetException(expected);

        var failure = await Assert.ThrowsExactlyAsync<AssertFailedException>(() => assertion);

        Assert.AreNotSame(expected, failure.InnerException);
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task AllCompleteAsync_CallFaultsWhileAnotherIsPending_WaitsForBothBeforePropagating()
    {
        var expected = new InvalidOperationException("call failed");
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var assertion = Assert.AllCompleteAsync([Task.FromException(expected), pending.Task], Deadline,
            new FakeTimeProvider(), testContext.CancellationToken);

        Assert.IsFalse(assertion.IsCompleted);

        pending.SetResult();
        var actual = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => assertion);
        Assert.AreSame(expected, actual);
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task AllCompleteAsync_DeadlineWithFaultedAndPendingCalls_ReportsBoth()
    {
        var clock = new FakeTimeProvider();
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var assertion = Assert.AllCompleteAsync([Task.FromException(new InvalidOperationException("call failed")), pending.Task],
            Deadline, clock, testContext.CancellationToken);
        clock.Advance(Deadline);

        var failure = await Assert.ThrowsExactlyAsync<AssertFailedException>(() => assertion);

        Assert.AreEqual(string.Join(Environment.NewLine,
            "Not all calls completed within 00:00:30.",
            "Call states observed after the deadline:",
            "[0] Faulted",
            "    InvalidOperationException: call failed",
            "[1] WaitingForActivation"), failure.Message);
        pending.SetResult();
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task AllCompleteAsync_CallerCancels_PropagatesTheCallerToken()
    {
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(testContext.CancellationToken);
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var assertion = Assert.AllCompleteAsync([pending.Task], Deadline, new FakeTimeProvider(), caller.Token);
        await caller.CancelAsync();

        var failure = await Assert.ThrowsAsync<OperationCanceledException>(() => assertion);

        Assert.AreEqual(caller.Token, failure.CancellationToken);
        pending.SetResult();
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task AllCompleteAsync_CancelledCall_PropagatesTheCallToken()
    {
        using var call = new CancellationTokenSource();
        await call.CancelAsync();

        var failure = await Assert.ThrowsAsync<OperationCanceledException>(() => Assert.AllCompleteAsync(
            [Task.FromCanceled(call.Token)], Deadline, new FakeTimeProvider(), testContext.CancellationToken));

        Assert.AreEqual(call.Token, failure.CancellationToken);
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task AllCompleteAsync_ExpectedExceptionAssertion_CompletesSuccessfully()
    {
        var expectedFailure = Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => Task.FromException(
            new ObjectDisposedException("fixture")));

        await Assert.AllCompleteAsync([expectedFailure], Deadline, new FakeTimeProvider(), testContext.CancellationToken);
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task AllCompleteAsync_CollectionChangesAfterCall_KeepsTheOriginalCalls()
    {
        var clock = new FakeTimeProvider();
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        List<Task> calls = [pending.Task];
        var assertion = Assert.AllCompleteAsync(calls, Deadline, clock, testContext.CancellationToken);
        calls[0] = Task.CompletedTask;
        clock.Advance(Deadline);

        var failure = await Assert.ThrowsExactlyAsync<AssertFailedException>(() => assertion);

        Assert.Contains("[0] WaitingForActivation", failure.Message);
        pending.SetResult();
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task AllCompleteAsync_ZeroDeadlineWithCompletedCalls_CompletesSuccessfully()
    {
        await Assert.AllCompleteAsync([Task.CompletedTask], TimeSpan.Zero,
            new FakeTimeProvider(), testContext.CancellationToken);
    }

    [TestMethod]
    public async Task AllCompleteAsync_NullCalls_RejectsTheArgument()
    {
        var failure = await Assert.ThrowsExactlyAsync<ArgumentNullException>(() => Assert.AllCompleteAsync(
            null!, Deadline, new FakeTimeProvider(), testContext.CancellationToken));

        Assert.AreEqual("calls", failure.ParamName);
    }

    [TestMethod]
    public async Task AllCompleteAsync_NullTimeProvider_RejectsTheArgument()
    {
        var failure = await Assert.ThrowsExactlyAsync<ArgumentNullException>(() => Assert.AllCompleteAsync(
            [], Deadline, null!, testContext.CancellationToken));

        Assert.AreEqual("timeProvider", failure.ParamName);
    }

    [TestMethod]
    public async Task AllCompleteAsync_InvalidDeadline_RejectsTheArgument()
    {
        var failure = await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() => Assert.AllCompleteAsync(
            [], TimeSpan.FromMilliseconds(-2), new FakeTimeProvider(), testContext.CancellationToken));

        Assert.AreEqual("timeout", failure.ParamName);
    }
}
