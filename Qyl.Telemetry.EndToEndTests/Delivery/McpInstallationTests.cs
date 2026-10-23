namespace Qyl.Telemetry.EndToEndTests.Delivery;

[TestClass]
public sealed class McpInstallationTests(TestContext context)
{
    [TestMethod]
    [DataRow("main.js")]
    [DataRow("dependency.js")]
    public async Task McpInstallation_RuntimeBytesChange_RejectsTheInstallation(string changedFile)
    {
        var directory = Path.Combine(context.TestTempDirectory!, "installed-runtime");
        Directory.CreateDirectory(directory);
        var entryPoint = Path.Combine(directory, "main.js");
        await File.WriteAllTextAsync(entryPoint, "entrypoint", context.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(directory, "dependency.js"), "dependency", context.CancellationToken);
        var installation = new McpInstallation(directory, entryPoint, ArtifactFingerprint.Directory(directory));
        await File.WriteAllTextAsync(Path.Combine(directory, changedFile), "changed", context.CancellationToken);
        _ = Assert.ThrowsExactly<InvalidDataException>(installation.Verify);
    }
}
