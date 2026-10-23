using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;

namespace Qyl.Telemetry.Tests;

/// <summary>Retains a private process group/job until the command and all its descendants are terminated.</summary>
internal sealed partial class OwnedProcess : IAsyncDisposable
{
    private readonly Process _host;
    private readonly NamedPipeServerStream _control;
    private readonly Task<int> _exit;
    private readonly TimeProvider _time;
    private readonly Lazy<Task> _termination;
    private int _forced;
    private int _disposed;

    private OwnedProcess(Process host, NamedPipeServerStream control, int id, TimeProvider time)
    {
        _host = host;
        _control = control;
        Id = id;
        _time = time;
        _exit = ReadExitAsync();
        _termination = new Lazy<Task>(KillCoreAsync);
    }

    internal int Id { get; }
    internal bool HasExited => _exit.IsCompletedSuccessfully || Volatile.Read(ref _forced) != 0;
    internal int ExitCode => _exit.IsCompletedSuccessfully ? _exit.Result
        : Volatile.Read(ref _forced) != 0 ? -1 : throw new InvalidOperationException("The command is still running.");
    internal StreamReader StandardOutput => _host.StandardOutput;
    internal StreamReader StandardError => _host.StandardError;

    internal bool CloseMainWindow()
    {
        using var target = Process.GetProcessById(Id);
        return target.CloseMainWindow();
    }

    internal static async Task<OwnedProcess> StartAsync(ProcessStartInfo start, CancellationToken cancellationToken,
        TimeProvider? timeProvider = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var time = timeProvider ?? TimeProvider.System;
        var assembly = Path.Combine(AppContext.BaseDirectory, "process-host", "Qyl.Telemetry.ProcessHost.dll");
        if (!File.Exists(assembly)) throw new FileNotFoundException("The process ownership host was not staged.", assembly);
        var pipeName = CreatePipeName(Path.GetTempPath());
        var control = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var hostStart = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = start.WorkingDirectory,
            RedirectStandardInput = start.RedirectStandardInput,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = start.StandardOutputEncoding,
            StandardErrorEncoding = start.StandardErrorEncoding
        };
        hostStart.ArgumentList.Add(assembly);
        hostStart.ArgumentList.Add(pipeName);
        hostStart.Environment.Clear();
        foreach (var pair in start.Environment) hostStart.Environment.Add(pair);
        Process? host = null;
        var connected = false;
        try
        {
            host = Process.Start(hostStart) ?? throw new InvalidOperationException("The process ownership host did not start.");
            using var startup = new CancellationTokenSource(TimeSpan.FromSeconds(10), time);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, startup.Token);
            await control.WaitForConnectionAsync(deadline.Token).ConfigureAwait(false);
            connected = true;
            await using var request = new MemoryStream();
            await using (var writer = new BinaryWriter(request, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                writer.Write(start.FileName);
                writer.Write(start.Arguments);
                writer.Write(start.ArgumentList.Count);
                foreach (var argument in start.ArgumentList) writer.Write(argument);
            }
            var bytes = request.ToArray();
            var length = new byte[sizeof(int)];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
            await control.WriteAsync(length, deadline.Token).ConfigureAwait(false);
            await control.WriteAsync(bytes, deadline.Token).ConfigureAwait(false);
            await control.FlushAsync(deadline.Token).ConfigureAwait(false);
            var id = await ReadIntAsync(control, deadline.Token).ConfigureAwait(false);
            return id > 0 ? new OwnedProcess(host, control, id, time)
                : throw new InvalidOperationException($"Could not start {start.FileName}.");
        }
        catch (Exception startupFailure)
        {
            var failures = new List<Exception> { startupFailure };
            try
            {
                if (host is not null)
                {
                    Terminate(host, connected);
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10), time);
                    await host.WaitForExitAsync(cleanup.Token).ConfigureAwait(false);
                    var output = host.StandardOutput.ReadToEndAsync(cleanup.Token);
                    var error = host.StandardError.ReadToEndAsync(cleanup.Token);
                    await Task.WhenAll(output, error).ConfigureAwait(false);
                    startupFailure.Data["Output"] = output.Result;
                    startupFailure.Data["Error"] = error.Result;
                }
            }
            catch (Exception cleanupFailure) { failures.Add(cleanupFailure); }
            finally
            {
                await control.DisposeAsync().ConfigureAwait(false);
                host?.Dispose();
            }
            if (failures.Count > 1) throw new AggregateException("Process startup and cleanup failed.", failures);
            throw;
        }
    }

    internal static string CreatePipeName(string temporaryDirectory)
    {
        var name = Guid.NewGuid().ToString("N");
        if (OperatingSystem.IsWindows()
            || System.Text.Encoding.UTF8.GetByteCount(Path.Combine(temporaryDirectory, "CoreFxPipe_" + name)) < 104)
            return name;

        // .NET supports absolute Unix pipe paths for temporary directories exceeding sockaddr_un.sun_path.
        return Path.Combine("/tmp", "qyl-" + name);
    }

    internal async Task WaitForExitAsync(CancellationToken cancellationToken) =>
        _ = await _exit.WaitAsync(cancellationToken).ConfigureAwait(false);

    internal Task KillAsync(CancellationToken cancellationToken) => _termination.Value.WaitAsync(cancellationToken);

    private async Task KillCoreAsync()
    {
        if (!_exit.IsCompletedSuccessfully) Interlocked.Exchange(ref _forced, 1);
        Terminate(_host, groupEstablished: true);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10), _time);
        await _host.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
        // Observe the expected control-pipe closure after forced termination.
        try { _ = await _exit.WaitAsync(deadline.Token).ConfigureAwait(false); }
        catch (IOException) when (Volatile.Read(ref _forced) != 0) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try
        {
            await _termination.Value.ConfigureAwait(false);
        }
        finally
        {
            await _control.DisposeAsync().ConfigureAwait(false);
            _host.Dispose();
        }
    }

    private async Task<int> ReadExitAsync() => await ReadIntAsync(_control, CancellationToken.None).ConfigureAwait(false);

    private static async Task<int> ReadIntAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[sizeof(int)];
        await stream.ReadExactlyAsync(buffer, cancellationToken).ConfigureAwait(false);
        return System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(buffer);
    }

    private static void Terminate(Process host, bool groupEstablished)
    {
        if (!OperatingSystem.IsWindows() && groupEstablished)
        {
            if (Kill(-host.Id, 9) != 0 && !host.HasExited)
                throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
        else if (!host.HasExited)
        {
            try { host.Kill(); }
            catch (InvalidOperationException) when (host.HasExited) { }
        }
    }

    [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static partial int Kill(int process, int signal);
}
