namespace Qyl.Telemetry.Tests.Collector;

/// <summary>Gives each owned resource its cleanup attempt and collects the failures instead of stopping at the first.</summary>
internal static class CleanupErrors
{
    extension(List<Exception> errors)
    {
        internal void Attempt(Action cleanup)
        {
            try { cleanup(); }
            catch (Exception error) { errors.Add(error); }
        }

        internal async Task AttemptAsync(Func<ValueTask> cleanup)
        {
            try { await cleanup().ConfigureAwait(false); }
            catch (Exception error) { errors.Add(error); }
        }
    }
}
