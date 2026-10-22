using System.Globalization;
using System.Text;

namespace Qyl.Telemetry.Tests;

/// <summary>Completion assertions that preserve the outcomes of the calls being awaited.</summary>
[JetBrains.Annotations.PublicAPI]
public static class TaskCompletionAssertions
{
    extension(Assert)
    {
        /// <summary>
        /// Awaits every call within one deadline on <paramref name="timeProvider"/>. A call's own exception
        /// or cancellation propagates unchanged; when the deadline passes first, the assertion fails and
        /// names each call's index and state. The deadline stops the waiting, not the calls: the caller
        /// owns their cancellation and cleanup. To expect a call to fail, pass the task an MSTest
        /// exception assertion returns.
        /// </summary>
        public static async Task AllCompleteAsync(IReadOnlyList<Task> calls, TimeSpan timeout,
            TimeProvider timeProvider, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(calls);
            ArgumentNullException.ThrowIfNull(timeProvider);
            ArgumentOutOfRangeException.ThrowIfLessThan(timeout, TimeSpan.Zero);
            Task[] snapshot = [.. calls];

            var all = Task.WhenAll(snapshot);
            using var stopDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var deadline = Task.Delay(timeout, timeProvider, stopDeadline.Token);
            if (await Task.WhenAny(all, deadline).ConfigureAwait(false) == all)
            {
                await stopDeadline.CancelAsync().ConfigureAwait(false);
                await all.ConfigureAwait(false);
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();
            throw new AssertFailedException(DescribeDeadline(snapshot, timeout));
        }
    }

    private static string DescribeDeadline(Task[] calls, TimeSpan timeout)
    {
        var message = new StringBuilder()
            .Append(CultureInfo.InvariantCulture, $"Not all calls completed within {timeout:c}.")
            .AppendLine()
            .Append("Call states observed after the deadline:");
        foreach (var (index, call) in calls.Index())
        {
            message.AppendLine().Append(CultureInfo.InvariantCulture, $"[{index}] {call.Status}");
            if (call.Exception is not { } failure)
                continue;
            foreach (var inner in failure.Flatten().InnerExceptions)
                message.AppendLine().Append(CultureInfo.InvariantCulture, $"    {inner.GetType().Name}: {inner.Message}");
        }

        return message.ToString();
    }
}
