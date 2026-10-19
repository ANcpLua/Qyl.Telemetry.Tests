using System.Diagnostics;
using System.Text.Json;
using System.Xml.Linq;

namespace Qyl.Telemetry.IntegrationTests.Probes;

[TestClass]
public sealed class InvocationContractTests(TestContext context)
{
    private static string FixtureDirectory => Path.Combine(AppContext.BaseDirectory, "fixtures", "TelemetryInvocation.Probe");
    private static string ProbeAssembly => Path.Combine(FixtureDirectory, "TelemetryInvocation.Probe.dll");

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task Original_executor_rows_failures_and_inconclusive_results_survive_the_trace_scope()
    {
        var directory = context.TestTempDirectory;
        Assert.IsNotNull(directory);
        var result = await RunAsync("--report-trx", "--results-directory", directory);
        Assert.AreEqual(2, result.ExitCode, result.Error);
        var path = Assert.ContainsSingle(Directory.GetFiles(directory, "*.trx"));
        context.AddResultFile(path);
        var report = XDocument.Load(path);
        XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
        var results = report.Descendants(ns + "UnitTestResult").ToArray();
        Assert.HasCount(5, results);
        Assert.HasCount(5, results.Select(item => (string?)item.Attribute("testId")).Distinct());
        var passed = results.Where(item => (string?)item.Attribute("outcome") == "Passed").ToArray();
        Assert.HasCount(2, passed);
        foreach (var value in new[] { ProbeContract.FirstRow, ProbeContract.SecondRow })
        {
            var row = Assert.ContainsSingle(passed.Where(item => item.Value.Contains($"{ProbeContract.RowMarker}|{value}|", StringComparison.Ordinal)));
            Assert.AreEqual(1, row.Value.Split(ProbeContract.ExecutorMarker).Length - 1);
        }

        var rowIdentities = passed.Select(item => item.Value.Split(ProbeContract.RowMarker + "|")[1]
            .Split('|')[1].Split('\r', '\n')[0]).ToArray();
        Assert.HasCount(2, rowIdentities.Distinct(StringComparer.Ordinal));
        foreach (var identity in rowIdentities) Assert.AreEqual(32, identity.Length);
        RequireResult(ProbeContract.AssertionMethod, "Failed", ProbeContract.AssertionMessage);
        RequireResult(ProbeContract.ExceptionMethod, "Failed", ProbeContract.ExceptionMessage);
        RequireResult(ProbeContract.InconclusiveMethod, "NotExecuted", ProbeContract.InconclusiveMessage);
        return;

        void RequireResult(string method, string outcome, string message)
        {
            var item = Assert.ContainsSingle(results.Where(item => ((string?)item.Attribute("testName"))!
                .Contains(method, StringComparison.Ordinal)));
            Assert.AreEqual(outcome, (string?)item.Attribute("outcome"));
            Assert.Contains(message, item.Value);
        }
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task Discovery_reports_the_original_method_source_for_each_data_row()
    {
        var result = await RunAsync("--list-tests", "json");
        Assert.AreEqual(0, result.ExitCode, result.Error);
        using var discovery = JsonDocument.Parse(result.Output);
        var rows = discovery.RootElement.GetProperty("tests").EnumerateArray().Where(item =>
            item.GetProperty("type").GetProperty("methodName").GetString() == ProbeContract.RowMethod).ToArray();
        Assert.HasCount(2, rows);
        var source = await File.ReadAllLinesAsync(Path.Combine(FixtureDirectory, ProbeContract.CasesFileName), context.CancellationToken);
        var expectedLine = Array.FindIndex(source, line => line.Trim() == "[ProbeMethod]") + 1;
        Assert.IsGreaterThan(0, expectedLine);
        foreach (var row in rows)
        {
            var location = row.GetProperty("location");
            Assert.AreEqual(ProbeContract.CasesSourcePath, location.GetProperty("file").GetString());
            Assert.AreEqual(expectedLine, location.GetProperty("lineStart").GetInt32());
            Assert.AreEqual(expectedLine, location.GetProperty("lineEnd").GetInt32());
        }
    }

    private async Task<(int ExitCode, string Output, string Error)> RunAsync(params string[] arguments)
    {
        Assert.IsTrue(File.Exists(ProbeAssembly), $"MSBuild did not deploy {ProbeAssembly}.");
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add(ProbeAssembly);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the native probe.");
        // Drain redirected output after cancellation has killed and joined the child.
        var output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var error = process.StandardError.ReadToEndAsync(CancellationToken.None);
        try { await process.WaitForExitAsync(context.CancellationToken); }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
            await Task.WhenAll(output, error);
        }
        return (process.ExitCode, await output, await error);
    }
}
