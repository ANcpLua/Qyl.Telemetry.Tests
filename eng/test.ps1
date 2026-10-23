[CmdletBinding()]
param(
    [ValidateSet("Unit", "Integration", "EndToEnd", "All")]
    [string] $Suite = "Unit",
    [switch] $NoRestore,
    [switch] $NoBuild,
    [switch] $Coverage,
    [switch] $NativeAot,
    [string] $RuntimeIdentifier,
    [string] $Filter,
    [string] $ResultsDirectory
)

$ErrorActionPreference = "Stop"
$PSNativeCommandUseErrorActionPreference = $false
$repository = Split-Path -Parent $PSScriptRoot
$projects = @{
    Unit = "Qyl.Telemetry.UnitTests/Qyl.Telemetry.UnitTests.csproj"
    Integration = "Qyl.Telemetry.IntegrationTests/Qyl.Telemetry.IntegrationTests.csproj"
    EndToEnd = "Qyl.Telemetry.EndToEndTests/Qyl.Telemetry.EndToEndTests.csproj"
}
if (-not $env:DOTNET_PROCESSOR_COUNT) {
    $env:DOTNET_PROCESSOR_COUNT = "2"
}
if (-not $ResultsDirectory) {
    $ResultsDirectory = Join-Path $repository "artifacts/results/$Suite"
}
$ResultsDirectory = [System.IO.Path]::GetFullPath($ResultsDirectory, $repository)
if ($NativeAot -and $Suite -ne "Unit") {
    throw "NativeAOT runner execution is supported only for Unit. The other suites use custom [TracePerTest] classes that MSTest 4.5.1 source generation does not discover."
}
if ($NativeAot -and $Coverage) {
    throw "NativeAOT coverage requires a separately instrumented publication; run the verified managed coverage path without -NativeAot."
}
if ($Suite -eq "Unit") {
    $Filter = if ($Filter) { "(TestCategory=Fast)&($Filter)" } else { "TestCategory=Fast" }
}
$runnerArguments = @()
if ($Filter) { $runnerArguments += @("--filter", $Filter) }
$runnerArguments += @(
    "--report-trx", "--results-directory", $ResultsDirectory
)

Push-Location $repository
try {
    if ($NativeAot) {
        if (-not $RuntimeIdentifier) {
            $RuntimeIdentifier = [System.Runtime.InteropServices.RuntimeInformation]::RuntimeIdentifier
        }
        $nativeOutput = Join-Path $repository "artifacts/native/Unit/$RuntimeIdentifier"
        $nativeArtifacts = (Join-Path $repository "artifacts/native-build/$RuntimeIdentifier") + [System.IO.Path]::DirectorySeparatorChar
        if (-not $NoBuild) {
            $publishArguments = @(
                "publish", (Join-Path $repository $projects.Unit),
                "--configuration", "Release", "--runtime", $RuntimeIdentifier,
                "--artifacts-path", $nativeArtifacts,
                "-p:QylNativeTestRunner=true", "-m:1", "--output", $nativeOutput
            )
            if ($NoRestore) { $publishArguments += "--no-restore" }
            & dotnet @publishArguments
            if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
        }
        $executableName = if ($IsWindows) { "Qyl.Telemetry.UnitTests.exe" } else { "Qyl.Telemetry.UnitTests" }
        $nativeExecutable = Join-Path $nativeOutput $executableName
        if (-not (Test-Path $nativeExecutable -PathType Leaf)) {
            throw "The native Unit executable is missing at $nativeExecutable. Publish it by running without -NoBuild."
        }
        & $nativeExecutable @runnerArguments
        $result = $LASTEXITCODE
    }
    else {
        $arguments = @("test")
        if ($Suite -eq "All") {
            $arguments += @("--solution", (Join-Path $repository "Qyl.Telemetry.Tests.slnx"))
        }
        else {
            $arguments += @("--project", (Join-Path $repository $projects[$Suite]))
        }
        $arguments += @("--configuration", "Release", "--max-parallel-test-modules", "1") + $runnerArguments
        if ($NoRestore) { $arguments += "--no-restore" }
        if ($NoBuild) { $arguments += "--no-build" }
        if ($Coverage) { $arguments += @("--coverage", "--coverage-output-format", "cobertura") }
        & dotnet @arguments
        $result = $LASTEXITCODE
    }
}
finally {
    Pop-Location
}
exit $result
