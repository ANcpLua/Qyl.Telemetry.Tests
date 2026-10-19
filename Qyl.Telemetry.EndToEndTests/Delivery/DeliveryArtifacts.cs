using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Qyl.Telemetry.Tests.Collector;

namespace Qyl.Telemetry.EndToEndTests.Delivery;

[JetBrains.Annotations.UsedImplicitly]
internal sealed record SourceInput(string Repository, string Commit, string Version);
[JetBrains.Annotations.UsedImplicitly]
internal sealed record McpInput(string Name, string Version, string Integrity);
internal sealed record DeliveryInputs(SourceInput Conventions, SourceInput Instrumentation, SourceInput Collector, McpInput Mcp);
internal sealed record CandidatePackage(string Id, string Version, string Path, string Sha512);
internal sealed record PreparedDelivery(DeliveryInputs Inputs, PublishedExecutable ManagedApplication,
    PublishedExecutable NativeApplication, PublishedExecutable NativeCollector, PublishedExecutable SemanticProbe,
    IReadOnlyList<CandidatePackage> Packages, string McpEntryPoint, string ManifestPath);

/// <summary>Class-owned immutable publication; test invocations only borrow its artifacts.</summary>
internal sealed class DeliveryArtifacts(TestContext context)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly string[] ConventionsProjects =
    ["Qyl.Telemetry.SemanticConventions", "Qyl.Telemetry.SemanticConventions.Incubating", "Qyl.Telemetry.SemanticConventions.Analyzers"];
    private static readonly string[] InstrumentationProjects =
    ["Qyl.Telemetry.AutoInstrumentation", "Qyl.Telemetry.AutoInstrumentation.DiagnosticListeners", "Qyl.Telemetry.AutoInstrumentation.Hosting", "Qyl.Telemetry.Hosting"];
    private readonly CancellationToken _cancellation = context.CancellationToken;
    private string _directory = "";
    private int _command;

    internal async Task<PreparedDelivery> PrepareAsync()
    {
        var inputFile = Path.Combine(AppContext.BaseDirectory, "delivery-inputs.json");
        var inputText = await File.ReadAllTextAsync(inputFile, _cancellation);
        var inputs = JsonSerializer.Deserialize<DeliveryInputs>(inputText, Json)
            ?? throw new InvalidDataException("Delivery inputs are missing.");
        var project = (await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "delivery-project.txt"), _cancellation)).Trim();
        var applicationDirectory = Path.GetDirectoryName(project)!;
        var root = Path.GetFullPath(Path.Combine(applicationDirectory, "../../.."));
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
        await File.WriteAllTextAsync(Path.Combine(root, "artifacts", "candidate-inspection.json"), JsonSerializer.Serialize(new
        {
            Project = semanticProject,
            PackageOverrides = Path.Combine(_directory, Path.GetFileName(root) + ".packages.props"),
            Artifacts = Path.Combine(_directory, "applications", key, "build") + Path.DirectorySeparatorChar,
            RuntimeInformation.RuntimeIdentifier
        }, Json), _cancellation);
        var manifestPath = Path.Combine(_directory, "prepared-" + key + ".json");
        if (File.Exists(manifestPath))
        {
            var cached = JsonSerializer.Deserialize<PreparedDelivery>(await File.ReadAllTextAsync(manifestPath, _cancellation), Json)
                ?? throw new InvalidDataException("The prepared delivery manifest is empty.");
            Verify(cached);
            context.WriteLine($"Using verified delivery artifacts: {manifestPath}");
            return cached;
        }

        var feed = Path.Combine(_directory, "packages");
        Directory.CreateDirectory(feed);
        var conventions = await CheckoutAsync("conventions", inputs.Conventions);
        await PackAsync(conventions, ConventionsProjects, inputs.Conventions.Version, feed, null);
        var instrumentation = await CheckoutAsync("instrumentation", inputs.Instrumentation);
        var conventionPins = ConventionsProjects.ToDictionary(name => name, _ => inputs.Conventions.Version, StringComparer.Ordinal);
        var instrumentationProps = WritePackageOverrides(instrumentation, conventionPins);
        await PackAsync(instrumentation, InstrumentationProjects, inputs.Instrumentation.Version, feed, instrumentationProps);

        var pins = new Dictionary<string, string>(conventionPins, StringComparer.Ordinal);
        foreach (var name in InstrumentationProjects) pins.Add(name, inputs.Instrumentation.Version);
        var config = WriteNuGetConfig(feed, pins.Keys);
        var appProps = WritePackageOverrides(root, pins);
        var appArtifacts = Path.Combine(_directory, "applications", key);
        var appAssets = Path.Combine(appArtifacts, "build");
        var managed = Path.Combine(appArtifacts, "managed");
        var native = Path.Combine(appArtifacts, "native");
        var rid = RuntimeInformation.RuntimeIdentifier;
        string[] appOptions = ["--configuration", "Release", "--runtime", rid, "-p:RestoreConfigFile=" + config,
            "--artifacts-path", appAssets + Path.DirectorySeparatorChar, "-p:DirectoryPackagesPropsPath=" + appProps];
        await RunAsync("publish-managed-application", "dotnet", ["publish", project, .. appOptions,
            "--output", managed, "--self-contained", "false", "-p:PublishAot=false"], root);
        await RunAsync("publish-native-application", "dotnet", ["publish", project, .. appOptions,
            "--output", native, "--self-contained", "true", "-p:PublishAot=true"], root);
        var packages = ReadCandidatePackages(feed);
        VerifyRestore(appAssets, project, packages);
        var semanticNative = Path.Combine(appArtifacts, "semantic");
        await RunAsync("publish-semantic-consumer", "dotnet", ["publish", semanticProject, .. appOptions,
            "--output", semanticNative, "--self-contained", "true", "-p:PublishAot=true"], root);
        var semanticPackages = packages.Where(package => package.Id is "Qyl.Telemetry.SemanticConventions"
            or "Qyl.Telemetry.SemanticConventions.Incubating").ToArray();
        VerifyRestore(appAssets, semanticProject, semanticPackages);

        var collector = await CheckoutAsync("collector", inputs.Collector);
        var collectorProps = WritePackageOverrides(collector, pins);
        var nativeCollector = Path.Combine(_directory, "native-collector");
        var collectorIdentity = Path.Combine(nativeCollector, "identity.json");
        if (!File.Exists(collectorIdentity))
        {
            await RunAsync("publish-native-collector", "dotnet", ["publish",
            Path.Combine(collector, "services", "qyl.collector", "qyl.collector.csproj"),
            "--configuration", "Release", "--runtime", rid, "--self-contained", "true",
            "-maxcpucount:4", "-nodeReuse:false", "-p:UseSharedCompilation=false", "-p:ConcurrentBuild=false",
            "--output", nativeCollector, "--artifacts-path", Path.Combine(_directory, "collector-build") + Path.DirectorySeparatorChar,
            "-p:RestoreConfigFile=" + config, "-p:DirectoryPackagesPropsPath=" + collectorProps,
                "-p:QylAot=true"], collector);
            var identified = PublishedExecutable.Identify(Path.Combine(nativeCollector, "qyl.collector" + (OperatingSystem.IsWindows() ? ".exe" : "")), ExecutableFormat.NativeAot, rid);
            await File.WriteAllTextAsync(collectorIdentity, JsonSerializer.Serialize(identified, Json), _cancellation);
        }
        var collectorArtifact = JsonSerializer.Deserialize<PublishedExecutable>(await File.ReadAllTextAsync(collectorIdentity, _cancellation), Json)
            ?? throw new InvalidDataException("Native Collector identity is missing.");
        collectorArtifact.VerifyIntegrity();

        var mcp = Path.Combine(_directory, "mcp");
        Directory.CreateDirectory(mcp);
        if (!File.Exists(Path.Combine(mcp, "node_modules", inputs.Mcp.Name, "dist", "main.js")))
            await RunAsync("install-published-mcp", "npm", ["install", "--prefix", mcp,
                "--ignore-scripts", "--no-audit", "--no-fund", "--save-exact", $"{inputs.Mcp.Name}@{inputs.Mcp.Version}"], root);
        using (var npm = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(mcp, "package-lock.json"), _cancellation)))
        {
            var package = npm.RootElement.GetProperty("packages").GetProperty("node_modules/" + inputs.Mcp.Name);
            if (package.GetProperty("version").GetString() != inputs.Mcp.Version
                || package.GetProperty("integrity").GetString() != inputs.Mcp.Integrity)
                throw new InvalidDataException("The MCP package does not match its pinned registry identity.");
        }
        var executableExtension = OperatingSystem.IsWindows() ? ".exe" : "";
        var prepared = new PreparedDelivery(inputs,
            PublishedExecutable.Identify(Path.Combine(managed, "TelemetryProbe.dll"), ExecutableFormat.ManagedAssembly, rid),
            PublishedExecutable.Identify(Path.Combine(native, "TelemetryProbe" + executableExtension), ExecutableFormat.NativeAot, rid),
            collectorArtifact,
            PublishedExecutable.Identify(Path.Combine(semanticNative, "SemanticContractProbe" + executableExtension), ExecutableFormat.NativeAot, rid),
            packages, Path.Combine(mcp, "node_modules", inputs.Mcp.Name, "dist", "main.js"), manifestPath);
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

    private async Task PackAsync(string repository, IEnumerable<string> names, string version, string feed, string? props)
    {
        var config = WriteNuGetConfig(feed, ConventionsProjects.Concat(InstrumentationProjects)
            .Where(name => Directory.EnumerateFiles(feed, name + ".*.nupkg").Any()));
        foreach (var name in names)
        {
            if (File.Exists(Path.Combine(feed, name + "." + version + ".nupkg"))) continue;
            List<string> arguments = ["pack", Path.Combine(repository, "src", name, name + ".csproj"),
                "--configuration", "Release", "--output", feed, "-p:Version=" + version, "-p:PackageVersion=" + version,
                "-p:RestoreConfigFile=" + config];
            if (props is not null) arguments.Add("-p:DirectoryPackagesPropsPath=" + props);
            await RunAsync("pack-" + name, "dotnet", arguments, repository);
        }
    }

    private string WriteNuGetConfig(string feed, IEnumerable<string> packages)
    {
        var path = Path.Combine(_directory, "NuGet.Config");
        var selected = packages.ToArray();
        new XDocument(new XElement("configuration",
            new XElement("packageSources", new XElement("clear"),
                new XElement("add", new XAttribute("key", "candidate"), new XAttribute("value", feed)),
                new XElement("add", new XAttribute("key", "nuget.org"), new XAttribute("value", "https://api.nuget.org/v3/index.json"))),
            new XElement("packageSourceMapping",
                selected.Length == 0 ? null : new XElement("packageSource", new XAttribute("key", "candidate"), selected.Select(name =>
                    new XElement("package", new XAttribute("pattern", name)))),
                new XElement("packageSource", new XAttribute("key", "nuget.org"), new XElement("package", new XAttribute("pattern", "*")))))).Save(path);
        return path;
    }

    private string WritePackageOverrides(string repository, IReadOnlyDictionary<string, string> pins)
    {
        var path = Path.Combine(_directory, Path.GetFileName(repository) + ".packages.props");
        new XDocument(new XElement("Project",
            new XElement("Import", new XAttribute("Project", Path.Combine(repository, "Directory.Packages.props"))),
            new XElement("ItemGroup", pins.SelectMany(pin => new[]
            {
                new XElement("PackageVersion", new XAttribute("Remove", pin.Key)),
                new XElement("PackageVersion", new XAttribute("Include", pin.Key), new XAttribute("Version", pin.Value))
            })))).Save(path);
        return path;
    }

    private static List<CandidatePackage> ReadCandidatePackages(string feed)
    {
        var packages = new List<CandidatePackage>();
        foreach (var path in Directory.EnumerateFiles(feed, "*.nupkg"))
        {
            using var zip = ZipFile.OpenRead(path);
            using var nuspec = zip.Entries.Single(entry => entry.FullName.EndsWith(".nuspec", StringComparison.Ordinal)).Open();
            var metadata = XDocument.Load(nuspec).Descendants().Single(element => element.Name.LocalName == "metadata");
            var ns = metadata.Name.Namespace;
            var id = metadata.Element(ns + "id")?.Value ?? throw new InvalidDataException($"Package ID is missing: {path}");
            var version = metadata.Element(ns + "version")?.Value ?? throw new InvalidDataException($"Package version is missing: {path}");
            using var file = File.OpenRead(path);
            packages.Add(new CandidatePackage(id, version, path, Convert.ToBase64String(SHA512.HashData(file))));
        }
        return packages;
    }

    private static void VerifyRestore(string artifacts, string project, IReadOnlyList<CandidatePackage> packages)
    {
        var assets = Directory.EnumerateFiles(artifacts, "project.assets.json", SearchOption.AllDirectories).ToArray();
        var matched = false;
        foreach (var path in assets)
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.GetProperty("project").GetProperty("restore").GetProperty("projectPath").GetString() != project)
                continue;
            matched = true;
            var libraries = document.RootElement.GetProperty("libraries");
            foreach (var package in packages)
            {
                if (!libraries.TryGetProperty(package.Id + "/" + package.Version, out var library))
                    throw new InvalidDataException($"Candidate application did not restore {package.Id}/{package.Version}.");
                if (library.GetProperty("sha512").GetString() != package.Sha512)
                    throw new InvalidDataException($"Candidate package content differs from the packed file: {package.Id}.");
            }
        }
        if (!matched) throw new InvalidDataException($"No restore evidence was found for {project}.");
    }

    private static void Verify(PreparedDelivery prepared)
    {
        foreach (var artifact in new[] { prepared.ManagedApplication, prepared.NativeApplication, prepared.NativeCollector, prepared.SemanticProbe })
            artifact.VerifyIntegrity();
        foreach (var package in prepared.Packages)
        {
            using var file = File.OpenRead(package.Path);
            if (Convert.ToBase64String(SHA512.HashData(file)) != package.Sha512)
                throw new InvalidDataException($"Prepared candidate changed: {package.Path}");
        }
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
            WorkingDirectory = directory, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
        };
        var packageRoot = Path.Combine(_directory, "nuget-cache") + Path.DirectorySeparatorChar;
        start.Environment["NUGET_PACKAGES"] = packageRoot;
        start.Environment["NuGetPackageRoot"] = packageRoot;
        start.Environment["DOTNET_PROCESSOR_COUNT"] = "4";
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {command}.");
        var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
        Exception? failure = null;
        try { await process.WaitForExitAsync(_cancellation); }
        catch (Exception error) { failure = error; }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
            await Task.WhenAll(stdout, stderr);
            await File.WriteAllTextAsync(logPath, await stdout + await stderr, CancellationToken.None);
        }
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{label} exited {process.ExitCode}. See {logPath}\n{string.Join('\n', (await stdout + await stderr).Split('\n').TakeLast(20))}");
    }
}
