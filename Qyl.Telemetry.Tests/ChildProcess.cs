using System.Diagnostics;

namespace Qyl.Telemetry.Tests;

/// <summary>Runs a child process to its exit with redirected output; cancellation kills and joins the process tree.</summary>
internal static class ChildProcess
{
    public static async Task<(int ExitCode, string Output, string Error)> RunAsync(ProcessStartInfo start, string description,
        CancellationToken cancellationToken)
    {
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.UseShellExecute = false;
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {description}.");
        // Drain redirected output after cancellation has killed and joined the child.
        var output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var error = process.StandardError.ReadToEndAsync(CancellationToken.None);
        await using (new Reaper(process, output, error))
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        return (process.ExitCode, await output.ConfigureAwait(false), await error.ConfigureAwait(false));
    }

    private sealed class Reaper(Process process, Task<string> output, Task<string> error) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
            await Task.WhenAll(output, error).ConfigureAwait(false);
        }
    }
}
