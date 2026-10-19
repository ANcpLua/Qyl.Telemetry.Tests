# Qyl telemetry tests

Reusable test support and executable suites for Qyl-owned telemetry behavior.
The receiver originated in [Qyl.Telemetry.Testing](https://github.com/ANcpLua/Qyl.Telemetry.Testing).
Existing releases of that package are unchanged; this shared support project
is not currently published as its replacement.
The separate architecture demonstration lives in [architecture-template-fixtures](https://github.com/ANcpLua/architecture-template-fixtures).

Five projects protect Qyl-owned telemetry behavior across instrumentation,
conventions, capture, collection, storage and investigation.

| Project | Responsibility |
| --- | --- |
| `Qyl.Telemetry.Tests` | Shared library: canonical OTLP receiver, raw and typed evidence, trace graphs, assertions and isolated Collector/application fixtures. |
| `Qyl.Telemetry.Tests.Capture` | NativeAOT-compatible raw Activity snapshots, exact CLR-array assertions and versioned JSON evidence, without an MSTest or Collector dependency. |
| `Qyl.Telemetry.UnitTests` | Deterministic protocol interpretation, registry validation, graph selection and query diagnostics with explicit inputs. |
| `Qyl.Telemetry.IntegrationTests` | Real HTTP/gRPC and SDK export into the receiver, public `AddQyl()` registration, Collector lifecycle, persistence and pipeline isolation. |
| `Qyl.Telemetry.EndToEndTests` | Instrumented Qyl application-to-Collector delivery, selected capture and stored evidence. |

## Run

Install .NET SDK **10.0.401**, then run:

```sh
./build.sh
```

The default Fallout `Check` target builds once, executes the three suites with
Microsoft coverage, merges coverage with DotCov, and runs the pinned JetBrains
InspectCode tool across support, suites, fixture applications and the build host.
Coverage is informational; reported inspection findings fail the check.
Use `./build.sh --target Test`, `Coverage` or `InspectCode` for a focused target.

The suites execute through Microsoft.Testing.Platform
**2.5.1**, using **MSTest.Sdk 4.5.1**, net10.0 and the AllMicrosoft extension
profile (including Microsoft.CodeCoverage). The shared project is a library via
`IsTestApplication=false`. Dependency versions live in `Version.props`;
`Directory.Packages.props` maps packages to those properties.
`NuGet.Config` selects the public feed explicitly.
One `QylPlatformVersion` property selects both the Collector runtime and
Orchestration package. TRX and individual coverage reports are written to
`artifacts/results`; merged coverage is in `artifacts/coverage`, and inspection
findings are in `artifacts/inspection.sarif`.
For a focused run, use the native MTP options:

```sh
dotnet test --project Qyl.Telemetry.UnitTests/Qyl.Telemetry.UnitTests.csproj -c Release
```

Integration and end-to-end projects restore the host-RID Qyl runtime package and
copy its Collector payload into their output. No sibling checkout, local feed,
container daemon or globally installed `qyl` tool is required by the baseline.
Consumer assemblies register their own native lifecycle hooks. Unit cases do not
start a Collector; component cases that mutate process-wide configuration and
application scenarios have explicit sequential ownership.

## Delivery evidence

The suites exercise real exporters and receivers, native MSTest composition,
and application-to-Collector delivery. Each mutable application/Collector fixture
owns an isolated workspace and is disposed by its consuming test.

The delivered workload uses public pinned Qyl packages, a managed application,
and the packaged managed `qyl.collector.dll`. Investigation uses REST. Selected
capture cases verify field identity and array fidelity through storage; native
runner probes verify failures, data rows and source locations.

Candidate-package selection, published NativeAOT execution and MCP protocol
investigation remain separate delivery work. See [ownership](docs/port.md) and
[implemented APIs](docs/extension-api.md).

## Agent guidance

Start with [AGENTS.md](AGENTS.md). Each project owns its skill under skills/;
the root .agents/skills folders link to those sources for discovery. Reusable
policy design lives in the support skill's optional
[telemetry catalogue](Qyl.Telemetry.Tests/skills/qyl-telemetry-support/references/telemetry-policies.md).

[Build infrastructure](docs/build-infrastructure/README.md) describes MSBuild,
central packages and verification mechanics. [Implemented extension APIs](docs/extension-api.md)
distinguishes implemented fixtures and native composition from policy proposals.
