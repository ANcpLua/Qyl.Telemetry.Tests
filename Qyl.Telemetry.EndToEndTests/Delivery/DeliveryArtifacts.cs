using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Qyl.Telemetry.Tests.Collector;

namespace Qyl.Telemetry.EndToEndTests.Delivery;

[JetBrains.Annotations.UsedImplicitly]
internal sealed record SourceInput(string Repository, string Commit);
[JetBrains.Annotations.UsedImplicitly]
internal sealed record McpInput(string Name, string Version, string Integrity);
internal sealed record DeliveryInputs(SourceInput Collector, McpInput Mcp);
[JetBrains.Annotations.UsedImplicitly(JetBrains.Annotations.ImplicitUseTargetFlags.WithMembers)]
internal sealed record RestoredPackage(string Id, string Version, string Sha512);
internal sealed record PreparedDelivery(DeliveryInputs Inputs, PublishedExecutable ManagedApplication,
    PublishedExecutable NativeApplication, PublishedExecutable NativeCollector, PublishedExecutable SemanticProbe,
    IReadOnlyList<RestoredPackage> Packages, string McpEntryPoint, string ManifestPath);

/// <summary>
///     Class-owned immutable publication of the probes against the published packages pinned in Version.props;
///     test invocations only borrow its artifacts.
/// </summary>
internal sealed class DeliveryArtifacts(TestContext context)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly CancellationToken _cancellation = context.CancellationToken;
    private string _directory = "";
    private int _command;

    /// <summary>Installs the pinned npm MCP server once per version and returns its stdio entrypoint.</summary>
    internal async Task<string> InstallMcpAsync()
    {
        var (_, inputs, root, _) = await ReadInputsAsync();
        _directory = Path.Combine(root, "artifacts", "mcp", $"{inputs.Mcp.Name}-{inputs.Mcp.Version}");
        Directory.CreateDirectory(_directory);
        var entryPoint = Path.Combine(_directory, "node_modules", inputs.Mcp.Name, "dist", "main.js");
        if (!File.Exists(entryPoint))
            await RunAsync("install-published-mcp", "npm", ["install", "--prefix", _directory,
                "--ignore-scripts", "--no-audit", "--no-fund", "--save-exact", $"{inputs.Mcp.Name}@{inputs.Mcp.Version}"], root);
        using var npm = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(_directory, "package-lock.json"), _cancellation));
        var package = npm.RootElement.GetProperty("packages").GetProperty("node_modules/" + inputs.Mcp.Name);
        if (package.GetProperty("version").GetString() != inputs.Mcp.Version
            || package.GetProperty("integrity").GetString() != inputs.Mcp.Integrity)
            throw new InvalidDataException("The MCP package does not match its pinned registry identity.");
        return entryPoint;
    }

    private async Task<(string Text, DeliveryInputs Inputs, string Root, string Project)> ReadInputsAsync()
    {
        var inputText = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "delivery-inputs.json"), _cancellation);
        var inputs = JsonSerializer.Deserialize<DeliveryInputs>(inputText, Json)
            ?? throw new InvalidDataException("Delivery inputs are missing.");
        var project = (await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "delivery-project.txt"), _cancellation)).Trim();
        return (inputText, inputs, Path.GetFullPath(Path.Combine(Path.GetDirectoryName(project)!, "../../..")), project);
    }

    internal async Task<PreparedDelivery> PrepareAsync()
    {
        var (inputText, inputs, root, project) = await ReadInputsAsync();
        var applicationDirectory = Path.GetDirectoryName(project)!;
        var semanticProject = (await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "semantic-project.txt"), _cancellation)).Trim();
        var fingerprint = new StringBuilder(inputText).Append(RuntimeInformation.RuntimeIdentifier);
        foreach (var path in new[] { project, Path.Combine(applicationDirectory, "Program.cs"),
                     Path.Combine(root, "Qyl.Telemetry.EndToEndTests", "ProbeSignals.cs"),
                     Path.Combine(root, "Directory.Build.props"), Path.Combine(root, "Directory.Packages.props"),
                     Path.Combine(root, "global.json"), Path.Combine(root, "Version.props"),
                     semanticProject, Path.Combine(Path.GetDirectoryName(semanticProject)!, "Program.cs") })
            fingerprint.Append(await File.ReadAllTextAsync(path, _cancellation));
        var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprint.ToString())))[..16];
        var sourceKey = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(inputText + RuntimeInformation.RuntimeIdentifier)))[..16];
        _directory = Path.Combine(root, "artifacts", "delivery", sourceKey);
        Directory.CreateDirectory(_directory);
        var manifestPath = Path.Combine(_directory, "prepared-" + key + ".json");
        if (File.Exists(manifestPath))
        {
            var cached = JsonSerializer.Deserialize<PreparedDelivery>(await File.ReadAllTextAsync(manifestPath, _cancellation), Json)
                ?? throw new InvalidDataException("The prepared delivery manifest is empty.");
            Verify(cached);
            context.WriteLine($"Using verified delivery artifacts: {manifestPath}");
            return cached;
        }

        var appArtifacts = Path.Combine(_directory, "applications", key);
        var appAssets = Path.Combine(appArtifacts, "build");
        var managed = Path.Combine(appArtifacts, "managed");
        var native = Path.Combine(appArtifacts, "native");
        var rid = RuntimeInformation.RuntimeIdentifier;
        string[] appOptions = ["--configuration", "Release", "--runtime", rid, "--artifacts-path", appAssets + Path.DirectorySeparatorChar];
        await RunAsync("publish-managed-application", "dotnet", ["publish", project, .. appOptions,
            "--output", managed, "--self-contained", "false", "-p:PublishAot=false"], root);
        await RunAsync("publish-native-application", "dotnet", ["publish", project, .. appOptions,
            "--output", native, "--self-contained", "true", "-p:PublishAot=true"], root);
        var semanticNative = Path.Combine(appArtifacts, "semantic");
        await RunAsync("publish-semantic-consumer", "dotnet", ["publish", semanticProject, .. appOptions,
            "--output", semanticNative, "--self-contained", "true", "-p:PublishAot=true"], root);
        IReadOnlyList<RestoredPackage> packages = [.. RestoredPackages(appAssets, project), .. RestoredPackages(appAssets, semanticProject)];

        var collector = await CheckoutAsync("collector", inputs.Collector);
        var nativeCollector = Path.Combine(_directory, "native-collector");
        var collectorIdentity = Path.Combine(nativeCollector, "identity.json");
        if (!File.Exists(collectorIdentity))
        {
            await RunAsync("publish-native-collector", "dotnet", ["publish",
            Path.Combine(collector, "services", "qyl.collector", "qyl.collector.csproj"),
            "--configuration", "Release", "--runtime", rid, "--self-contained", "true",
            "-maxcpucount:4", "-nodeReuse:false", "-p:UseSharedCompilation=false", "-p:ConcurrentBuild=false",
            "--output", nativeCollector, "--artifacts-path", Path.Combine(_directory, "collector-build") + Path.DirectorySeparatorChar,
            "-p:QylAot=true"], collector);
            var identified = PublishedExecutable.Identify(Path.Combine(nativeCollector, "qyl.collector" + (OperatingSystem.IsWindows() ? ".exe" : "")), ExecutableFormat.NativeAot, rid);
            await File.WriteAllTextAsync(collectorIdentity, JsonSerializer.Serialize(identified, Json), _cancellation);
        }
        var collectorArtifact = JsonSerializer.Deserialize<PublishedExecutable>(await File.ReadAllTextAsync(collectorIdentity, _cancellation), Json)
            ?? throw new InvalidDataException("Native Collector identity is missing.");
        collectorArtifact.VerifyIntegrity();

        var mcpEntryPoint = await new DeliveryArtifacts(context).InstallMcpAsync();
        var executableExtension = OperatingSystem.IsWindows() ? ".exe" : "";
        var prepared = new PreparedDelivery(inputs,
            PublishedExecutable.Identify(Path.Combine(managed, "TelemetryProbe.dll"), ExecutableFormat.ManagedAssembly, rid),
            PublishedExecutable.Identify(Path.Combine(native, "TelemetryProbe" + executableExtension), ExecutableFormat.NativeAot, rid),
            collectorArtifact,
            PublishedExecutable.Identify(Path.Combine(semanticNative, "SemanticContractProbe" + executableExtension), ExecutableFormat.NativeAot, rid),
            packages, mcpEntryPoint, manifestPath);
        Verify(prepared);
        await File.WriteAllTextAsync(manifestPath + ".tmp", JsonSerializer.Serialize(prepared, Json), _cancellation);
        File.Move(manifestPath + ".tmp", manifestPath, overwrite: true);
        return prepared;
    }

    private async Task<string> CheckoutAsync(string name, SourceInput input)
    {
        var directory = Path.Combine(_directory, "sources", name);
        if (!Directory.Exists(Path.Combine(directory, ".git")))
        {
            Directory.CreateDirectory(directory);
            await RunAsync(name + "-init", "git", ["init", "--quiet"], directory);
            await RunAsync(name + "-remote", "git", ["remote", "add", "origin", input.Repository], directory);
        }
        await RunAsync(name + "-fetch", "git", ["fetch", "--depth", "1", "origin", input.Commit], directory);
        await RunAsync(name + "-checkout", "git", ["-c", "advice.detachedHead=false", "checkout", "--detach", input.Commit], directory);
        return directory;
    }

    /// <summary>Records the published Qyl packages a probe actually restored.</summary>
    private static IEnumerable<RestoredPackage> RestoredPackages(string artifacts, string project)
    {
        foreach (var path in Directory.EnumerateFiles(artifacts, "project.assets.json", SearchOption.AllDirectories))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.GetProperty("project").GetProperty("restore").GetProperty("projectPath").GetString() != project)
                continue;
            return [.. document.RootElement.GetProperty("libraries").EnumerateObject()
                .Where(library => library.Name.StartsWith("Qyl.", StringComparison.Ordinal))
                .Select(library => library.Name.Split('/') is [var id, var version]
                    ? new RestoredPackage(id, version, library.Value.GetProperty("sha512").GetString()!)
                    : throw new InvalidDataException($"Unexpected restored library '{library.Name}'."))];
        }
        throw new InvalidDataException($"No restore evidence was found for {project}.");
    }

    private static void Verify(PreparedDelivery prepared)
    {
        foreach (var artifact in new[] { prepared.ManagedApplication, prepared.NativeApplication, prepared.NativeCollector, prepared.SemanticProbe })
            artifact.VerifyIntegrity();
        if (!File.Exists(prepared.McpEntryPoint)) throw new FileNotFoundException("The published MCP entrypoint is missing.", prepared.McpEntryPoint);
    }

    private async Task RunAsync(string label, string command, IReadOnlyList<string> arguments, string directory)
    {
        var logs = Path.Combine(_directory, "logs");
        Directory.CreateDirectory(logs);
        var logPath = Path.Combine(logs, $"{++_command:D2}-{label}.log");
        context.WriteLine($"{label}: {logPath}");
        var start = new ProcessStartInfo(command)
        {
            WorkingDirectory = directory, Environment = { ["DOTNET_PROCESSOR_COUNT"] = "4" }
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        var (exitCode, output, error) = await ChildProcess.RunAsync(start, command, _cancellation);
        var log = output + error;
        await File.WriteAllTextAsync(logPath, log, CancellationToken.None);
        if (exitCode != 0)
            throw new InvalidOperationException($"{label} exited {exitCode}. See {logPath}\n{string.Join('\n', log.Split('\n').TakeLast(20))}");
    }
}
