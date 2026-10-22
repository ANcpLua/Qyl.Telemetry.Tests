using System.Collections.Concurrent;

namespace Qyl.Telemetry.Tests.Policies;

/// <summary>Keeps the evidence text of a policy failure and returns the location the failure message cites.</summary>
internal interface IEvidenceStore
{
    string Save(string text);
}

/// <summary>Writes each evidence text to its own file under the system temp directory.</summary>
internal sealed class TempFileEvidenceStore : IEvidenceStore
{
    public static TempFileEvidenceStore Instance { get; } = new();

    public string Save(string text)
    {
        var directory = Path.Combine(Path.GetTempPath(), "qyl-telemetry-evidence", Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "telemetry-evidence.txt");
        File.WriteAllText(path, text);
        return path;
    }
}

/// <summary>Keeps evidence texts in memory so a policy failure can be inspected without the file system.</summary>
internal sealed class InMemoryEvidenceStore : IEvidenceStore
{
    private readonly ConcurrentDictionary<string, string> _saved = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, string> Saved => _saved;

    public string Save(string text)
    {
        var location = $"memory://telemetry-evidence/{Guid.NewGuid():N}";
        _saved[location] = text;
        return location;
    }
}
