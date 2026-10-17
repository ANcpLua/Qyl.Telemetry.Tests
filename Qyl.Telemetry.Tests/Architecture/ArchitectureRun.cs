using System.Runtime.ExceptionServices;

namespace Qyl.Telemetry.Tests.Architecture;

/// <summary>Owns one consuming assembly's lazily started telemetry and service processes.</summary>
internal sealed class ArchitectureRun(CancellationToken cancellationToken)
{
    private readonly CancellationTokenSource _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    private readonly Dictionary<Type, Task<RunningSystem>> _systems = [];
    private bool _closed;

    public SpanRecorder Recorder { get; } = new();

    public Task<RunningSystem> GetOrStartAsync(Type testClass, ArchitectureAttribute architecture,
        IReadOnlyList<BrickAttribute> bricks, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_systems)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            if (!_systems.TryGetValue(testClass, out var startup))
            {
                startup = RunningSystem.StartAsync(this, architecture, bricks, _lifetime.Token);
                _systems.Add(testClass, startup);
            }
            return startup.WaitAsync(cancellationToken);
        }
    }

    public async Task StopAllAsync()
    {
        Task<RunningSystem>[] pending;
        lock (_systems)
        {
            pending = [.. _systems.Values];
            _systems.Clear();
        }
        var stops = pending.Select(async startup =>
        {
            RunningSystem system;
            try { system = await startup.ConfigureAwait(false); }
            catch { return; } // Failed startup has already disposed its application.
            await system.DisposeAsync().ConfigureAwait(false);
        });
        var completion = Task.WhenAll(stops);
        try { await completion.ConfigureAwait(false); }
        catch { throw completion.Exception!; }
    }

    public async Task StopAsync()
    {
        Exception? serviceFailure = null;
        try { await StopAllAsync().ConfigureAwait(false); }
        catch (Exception error) { serviceFailure = error; }
        try { await Recorder.StopAsync().ConfigureAwait(false); }
        catch (Exception error) when (serviceFailure is not null)
        {
            throw new AggregateException("Service and telemetry shutdown both failed.", serviceFailure, error);
        }
        if (serviceFailure is not null)
        {
            ExceptionDispatchInfo.Capture(serviceFailure).Throw();
        }
    }

    public async Task CloseAsync()
    {
        lock (_systems) { _closed = true; }
        try
        {
            await _lifetime.CancelAsync().ConfigureAwait(false);
            await StopAsync().ConfigureAwait(false);
        }
        finally
        {
            try { await Recorder.DisposeAsync().ConfigureAwait(false); }
            finally { _lifetime.Dispose(); }
        }
    }
}
