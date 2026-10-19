using System.Collections.Concurrent;
using System.Diagnostics;
using OpenTelemetry;

namespace Qyl.Telemetry.Tests.Capture;

/// <summary>A source-filtered exporter for raw producer evidence, without OTLP conversion.</summary>
[JetBrains.Annotations.PublicAPI]
public sealed class ActivitySnapshotExporter(params string[] sources) : BaseExporter<Activity>
{
    private readonly HashSet<string> _sources = new(sources, StringComparer.Ordinal);
    private readonly ConcurrentQueue<ActivitySnapshot> _spans = [];

    /// <summary>Uses the queue's moment-in-time snapshot even while exports enqueue.</summary>
    // ReSharper disable once UseCollectionExpression
    public ActivitySnapshot[] GetSnapshots() => _spans.ToArray();

    public override ExportResult Export(in Batch<Activity> batch)
    {
        foreach (var activity in batch)
        {
            if (_sources.Contains(activity.Source.Name))
                _spans.Enqueue(ActivitySnapshot.Capture(activity));
        }
        return ExportResult.Success;
    }
}
