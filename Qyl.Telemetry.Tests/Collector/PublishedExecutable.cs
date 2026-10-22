using System.Diagnostics;
using System.Security.Cryptography;

namespace Qyl.Telemetry.Tests.Collector;

[JetBrains.Annotations.PublicAPI]
public enum ExecutableFormat { ManagedAssembly, NativeAot }

/// <summary>An identified executable whose format determines how the fixture launches it.</summary>
[JetBrains.Annotations.PublicAPI]
public sealed record PublishedExecutable(string Path, ExecutableFormat Format, string RuntimeIdentifier, string Sha256)
{
    /// <summary>Identifies the managed assembly at <paramref name="path" /> for the current runtime identifier.</summary>
    public static PublishedExecutable Managed(string path) =>
        Identify(path, ExecutableFormat.ManagedAssembly, System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier);

    public static PublishedExecutable Identify(string path, ExecutableFormat format, string runtimeIdentifier)
    {
        using var file = File.OpenRead(path);
        return new PublishedExecutable(System.IO.Path.GetFullPath(path), format, runtimeIdentifier,
            Convert.ToHexStringLower(SHA256.HashData(file)));
    }

    internal string Command => Format == ExecutableFormat.ManagedAssembly ? "dotnet" : Path;
    internal string[] Arguments => Format == ExecutableFormat.ManagedAssembly ? [Path] : [];

    internal void VerifyIntegrity()
    {
        using var file = File.OpenRead(Path);
        if (!string.Equals(Sha256, Convert.ToHexStringLower(SHA256.HashData(file)), StringComparison.Ordinal))
            throw new InvalidDataException($"Published artifact changed after identification: {Path}");
    }

    internal ProcessStartInfo StartInfo()
    {
        VerifyIntegrity();
        var start = new ProcessStartInfo(Command) { UseShellExecute = false, WorkingDirectory = System.IO.Path.GetDirectoryName(Path) };
        foreach (var argument in Arguments) start.ArgumentList.Add(argument);
        return start;
    }
}
