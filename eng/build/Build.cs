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
        DotNetBuild(settings => settings.SetProjectFile(_solution).SetConfiguration("Release")));

    [UsedImplicitly]
    private Target Test => target => target.DependsOn(Compile).Executes(() =>
        DotNet($"test --solution \"{_solution}\" -c Release --no-build --no-restore --report-trx --results-directory \"{ResultsDirectory}\""));

    private Target Coverage => target => target.DependsOn(Compile).Executes(() =>
    {
        CoverageDirectory.CreateOrCleanDirectory();
        ResultsDirectory.CreateOrCleanDirectory();
        DotNet($"test --solution \"{_solution}\" -c Release --no-build --no-restore --report-trx --results-directory \"{ResultsDirectory}\" --coverage --coverage-output-format cobertura");
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
        DotNet($"tool run jb -- inspectcode \"{RootDirectory / "eng/inspection.slnx"}\" --no-build --no-updates --disable-settings-layers=GlobalAll --properties=Configuration=Release --severity=INFO --format=Sarif --output=\"{report}\"");
        ValidateInspection(report);
    });

    private Target InspectCandidate => target => target.DependsOn(Coverage).Executes(() =>
    {
        using var context = JsonDocument.Parse(File.ReadAllText(RootDirectory / "artifacts/candidate-inspection.json"));
        var root = context.RootElement;
        var project = root.GetProperty("project").GetString();
        var properties = $"Configuration=Release;DirectoryPackagesPropsPath={root.GetProperty("packageOverrides").GetString()};ArtifactsPath={root.GetProperty("artifacts").GetString()};RuntimeIdentifier={root.GetProperty("runtimeIdentifier").GetString()}";
        var report = RootDirectory / "artifacts/candidate-inspection.sarif";
        DotNet($"tool run jb -- inspectcode \"{project}\" --no-build --no-updates --disable-settings-layers=GlobalAll --properties=\"{properties}\" --severity=INFO --format=Sarif --output=\"{report}\"");
        ValidateInspection(report);
    });

    private static void ValidateInspection(AbsolutePath report)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(report));
        var runs = document.RootElement.GetProperty("runs").EnumerateArray().ToArray();
        if (runs.Length == 0 || runs.Any(run => run.TryGetProperty("invocations", out var invocations)
            && invocations.EnumerateArray().Any(invocation => invocation.TryGetProperty("executionSuccessful", out var success)
                && success.ValueKind == JsonValueKind.False)))
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

    private Target Check => target => target.DependsOn(Coverage, InspectCode, InspectCandidate);
}
