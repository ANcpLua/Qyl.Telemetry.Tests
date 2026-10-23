using System.Text;
using System.Text.Json;

namespace Qyl.Telemetry.Tests.Collector;

/// <summary>A project-bound credential for a Collector fixture's ingest and read operations.</summary>
[JetBrains.Annotations.PublicAPI]
public sealed record CollectorProjectKey(string ProjectId, string Key);

/// <summary>Configures an isolated Collector without inheriting the caller's environment.</summary>
[JetBrains.Annotations.PublicAPI]
public sealed class CollectorFixtureOptions
{
    /// <summary>
    /// Project credentials with ingest and read roles. A nonempty list starts the Collector in ApiKey mode;
    /// an empty list retains the unsecured fixture used by existing capture tests.
    /// </summary>
    public IReadOnlyList<CollectorProjectKey> ProjectKeys { get; init; } = [];

    internal string? ProjectKeysJson()
    {
        ArgumentNullException.ThrowIfNull(ProjectKeys);
        if (ProjectKeys.Count == 0) return null;
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var binding in ProjectKeys)
        {
            ArgumentNullException.ThrowIfNull(binding);
            ArgumentException.ThrowIfNullOrWhiteSpace(binding.ProjectId);
            ArgumentException.ThrowIfNullOrWhiteSpace(binding.Key);
            if (binding.ProjectId != binding.ProjectId.Trim())
                throw new ArgumentException("A Collector project ID must not contain leading or trailing whitespace.", nameof(ProjectKeys));
            if (!keys.Add(binding.Key))
                throw new ArgumentException("Every Collector fixture API key must be unique.", nameof(ProjectKeys));
        }

        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            foreach (var project in ProjectKeys.GroupBy(binding => binding.ProjectId, StringComparer.Ordinal))
            {
                json.WriteStartArray(project.Key);
                foreach (var binding in project)
                {
                    json.WriteStartObject();
                    json.WriteString("key", binding.Key);
                    json.WriteStartArray("roles");
                    json.WriteStringValue("ingest");
                    json.WriteStringValue("read");
                    json.WriteEndArray();
                    json.WriteEndObject();
                }
                json.WriteEndArray();
            }
            json.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
