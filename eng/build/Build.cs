using System.Xml.Linq;
using System.Text.Json;
using DotCov;
using DotCov.Formatters;
using Fallout.Common;
using Fallout.Common.IO;
using Fallout.Common.Tools.DotNet;
using Fallout.Solutions;
using JetBrains.Annotations;
using Serilog;
using static Fallout.Common.Tools.DotNet.DotNetTasks;

namespace _build;

[UsedImplicitly]
internal sealed class Build : FalloutBuild
{
    public static int Main() => Execute<Build>(build => build.Check);

    [Solution("Qyl.Telemetry.Tests.slnx", GenerateProjects = false)]
    private readonly Solution _solution = null!;

    private static AbsolutePath CoverageDirectory => RootDirectory / "artifacts/coverage";
    private static AbsolutePath ResultsDirectory => RootDirectory / "artifacts/results";

    [UsedImplicitly]
    private Target Compile => target => target.Executes(() =>
    {
        ValidateSolutionProjects();
        DotNet($"build \"{_solution}\" --configuration Release -p:ShouldUnsetParentConfigurationAndPlatform=false -maxcpucount:1 -nodeReuse:false -p:UseSharedCompilation=false");
    });

    [UsedImplicitly]
    private Target Test => target => target.DependsOn(Compile).Executes(() =>
        RunTests(coverage: false));

    private Target Coverage => target => target.DependsOn(Compile).Executes(() =>
    {
        CoverageDirectory.CreateOrCleanDirectory();
        ResultsDirectory.CreateOrCleanDirectory();
        RunTests(coverage: true);
        var inputs = ReportResolver.Resolve(ResultsDirectory, ReportPattern.Parse("*.cobertura.xml"));
        var report = CoberturaParser.Parse(inputs);
        if (report.Files.Count == 0)
        {
            throw new InvalidOperationException("The test run produced no coverage measurements.");
        }

        foreach (var warning in report.Warnings)
        {
            Log.Warning("Coverage: {Warning}", warning.Detail);
        }

        var reportPath = CoverageDirectory / "coverage.json";
        File.WriteAllText(reportPath, JsonFormatter.Format(report));
        Log.Information("Merged {Inputs} coverage reports for {Files} files into {Report} (informational)",
            inputs.Count, report.Files.Count, reportPath);
    });

    private Target InspectCode => target => target.DependsOn(Compile).Executes(() =>
    {
        var report = RootDirectory / "artifacts/inspection.sarif";
        File.Delete(report);
        DotNet($"tool run jb -- inspectcode \"{_solution}\" --no-build --no-updates --disable-settings-layers=GlobalAll --properties=Configuration=Release --severity=INFO --format=Sarif --output=\"{report}\"");
        ValidateInspection(report);
    });

    private static void ValidateSolutionProjects()
    {
        string[] expected =
        [
            "Qyl.Telemetry.Tests/Qyl.Telemetry.Tests.csproj",
            "Qyl.Telemetry.UnitTests/Qyl.Telemetry.UnitTests.csproj",
            "Qyl.Telemetry.IntegrationTests/Qyl.Telemetry.IntegrationTests.csproj",
            "Qyl.Telemetry.EndToEndTests/Qyl.Telemetry.EndToEndTests.csproj"
        ];
        var actual = XDocument.Load(RootDirectory / "Qyl.Telemetry.Tests.slnx").Descendants("Project")
            .Select(project => (string?)project.Attribute("Path") ?? "").Order(StringComparer.Ordinal);
        if (!actual.SequenceEqual(expected.Order(StringComparer.Ordinal)))
            throw new InvalidOperationException("The solution must contain exactly the four primary telemetry projects.");
    }

    private static void RunTests(bool coverage)
    {
        var start = new System.Diagnostics.ProcessStartInfo("pwsh")
        {
            WorkingDirectory = RootDirectory,
            UseShellExecute = false
        };
        foreach (var argument in new[] { "-NoProfile", "-File", (RootDirectory / "eng/test.ps1").ToString(),
                     "-Suite", "All", "-NoRestore", "-NoBuild", "-ResultsDirectory", ResultsDirectory.ToString() })
            start.ArgumentList.Add(argument);
        if (coverage) start.ArgumentList.Add("-Coverage");
        using var process = System.Diagnostics.Process.Start(start)
            ?? throw new InvalidOperationException("PowerShell could not start the repository test entry point.");
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"The repository test entry point exited with {process.ExitCode}.");
    }

    private static void ValidateInspection(AbsolutePath report)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(report));
        var runs = document.RootElement.GetProperty("runs").EnumerateArray().ToArray();
        if (runs.Length == 0 || runs.Any(run => !run.TryGetProperty("invocations", out var invocations)
            || invocations.GetArrayLength() == 0
            || invocations.EnumerateArray().Any(invocation => !invocation.TryGetProperty("executionSuccessful", out var success)
                || success.ValueKind != JsonValueKind.True)))
        {
            throw new InvalidOperationException($"InspectCode did not report a successful analysis: {report}");
        }

        var count = runs
            .Sum(run => run.TryGetProperty("results", out var results) ? results.GetArrayLength() : 0);
        if (count != 0)
        {
            throw new InvalidOperationException($"InspectCode reported {count} findings: {report}");
        }
    }

    private Target Check => target => target.DependsOn(Coverage, InspectCode);
}
