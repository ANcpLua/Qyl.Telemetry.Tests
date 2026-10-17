using System.Diagnostics;
using System.Runtime.CompilerServices;
using JetBrains.Annotations;

namespace Qyl.Telemetry.Tests;

[PublicAPI]
public static class QylTest
{
    public static Task<QylTrace> TraceAsync(Func<QylCapture, Task> action, Func<QylTrace, bool> until,
        TimeSpan? timeout = null, CancellationToken cancellationToken = default) =>
        TraceAsync(action, until, OtlpReceiver.DefaultMaxPayloadBytes, timeout, cancellationToken);

    public static async Task<QylTrace> TraceAsync(Func<QylCapture, Task> action, Func<QylTrace, bool> until,
        int maxPayloadBytes, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(until);
        await using var receiver = await OtlpReceiver.StartAsync(maxPayloadBytes, cancellationToken).ConfigureAwait(false);
        using var ownedRoot = Activity.Current?.OperationName == QylTestHooks.Operation ? null : QylTestHooks.NewRoot();
        var root = Activity.Current!;
        var capture = new QylCapture(receiver, root.TraceId.ToHexString(), root.Id!);
        await action(capture).ConfigureAwait(false);
        var snapshot = await receiver
            .AwaitAsync(s => until(capture.Read(s)), timeout ?? TimeSpan.FromSeconds(10), cancellationToken)
            .ConfigureAwait(false);
        return capture.Read(snapshot);
    }
}

[PublicAPI]
public sealed class QylCapture
{
    private readonly OtlpReceiver _receiver;

    internal QylCapture(OtlpReceiver receiver, string traceId, string traceParent)
    {
        _receiver = receiver;
        TraceId = traceId;
        TraceParent = traceParent;
        Partition = new MetricPartition("qyl.test.id", traceId);
    }

    public Uri Endpoint => _receiver.Endpoint;
    public Uri GrpcEndpoint => _receiver.GrpcEndpoint;
    public string TraceId { get; }
    public string TraceParent { get; }
    public MetricPartition Partition { get; }

    public QylTrace Read()
    {
        return Read(_receiver.Snapshot());
    }

    internal QylTrace Read(TelemetrySnapshot snapshot)
    {
        return new QylTrace(TraceId, snapshot, Partition);
    }

    public Task<TelemetrySnapshot> AwaitAsync(Func<TelemetrySnapshot, bool> until, TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        return _receiver.AwaitAsync(until, timeout, cancellationToken);
    }
}

[PublicAPI]
public static class QylTestHooks
{
    internal const string Operation = "qyl.test";
    public const string TraceIdProperty = "Qyl.Telemetry.Tests.TraceId";

    public static void Initialize(TestContext context)
    {
        context.Properties[TraceIdProperty] = Activity.Current?.TraceId.ToHexString();
    }

    public static void Cleanup(TestContext context)
    {
        context.Properties.Remove(TraceIdProperty);
    }

    internal static Activity NewRoot()
    {
        return new Activity(Operation).SetIdFormat(ActivityIdFormat.W3C)
            .SetParentId(ActivityTraceId.CreateRandom(), default, ActivityTraceFlags.Recorded).Start();
    }
}

[PublicAPI]
public sealed class QylTelemetryAttribute : TestClassAttribute
{
    public override TestMethodAttribute GetTestMethodAttribute(TestMethodAttribute testMethodAttribute)
    {
        ArgumentNullException.ThrowIfNull(testMethodAttribute);
        return new QylTelemetryMethodAttribute(testMethodAttribute);
    }
}

internal sealed class QylTelemetryMethodAttribute(
    TestMethodAttribute inner,
    [CallerFilePath] string file = "",
    [CallerLineNumber] int line = -1) : TestMethodAttribute(file, line)
{
    public override async Task<TestResult[]> ExecuteAsync(ITestMethod testMethod)
    {
        using var root = QylTestHooks.NewRoot();
        return await inner.ExecuteAsync(testMethod).ConfigureAwait(false);
    }
}