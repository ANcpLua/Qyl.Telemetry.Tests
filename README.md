# Qyl telemetry tests

Active successor to [Qyl.Telemetry.Testing](https://github.com/ANcpLua/Qyl.Telemetry.Testing)
and [mstest-architecture-prototype](https://github.com/ANcpLua/mstest-architecture-prototype).
Their reusable receiver and architecture code is maintained here. Existing
`Qyl.Telemetry.Testing` NuGet releases remain available to pinned consumers;
this repository's shared support project is not currently published as a replacement package.

Four projects protect Qyl-owned telemetry behavior across instrumentation,
conventions, capture, collection, storage and investigation.

| Project | Responsibility |
| --- | --- |
| `Qyl.Telemetry.Tests` | Shared library: canonical OTLP receiver, raw and typed evidence, trace graphs, assertions and reusable architecture fixture primitives. |
| `Qyl.Telemetry.UnitTests` | Deterministic protocol interpretation, registry validation, graph selection, architecture rules and query diagnostics with explicit inputs. |
| `Qyl.Telemetry.IntegrationTests` | Real HTTP/gRPC and SDK export into the receiver, public `AddQyl()` registration, Collector lifecycle, persistence and recorder isolation. |
| `Qyl.Telemetry.EndToEndTests` | Retained Orders/Payments applications and assembled architecture, failure, hosting, log and metric scenarios. |

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

The v16 package baseline passes **278 cases** through the solution entrypoint
(verified 2026-10-18). It includes
real exporter/receiver transport and application-to-Collector persistence.
The retained Collector fixture currently launches the packaged **managed**
`qyl.collector.dll`; investigation assertions use the Collector's REST API.
This is managed execution against pinned public packages.
The v16-specific selected-header/metadata regression scenarios remain follow-up work.

Candidate-package restore, execution of a published Native AOT Collector binary,
and MCP protocol investigation remain subsequent delivery work. MTP execution,
AOT publishing and MCP invocation are separate evidence boundaries. See
[the ownership map](docs/port.md) for retained and deferred source responsibilities.

## Agent guidance

Start with [AGENTS.md](AGENTS.md). Each project owns its skill under skills/;
the root .agents/skills folders link to those sources for discovery. Reusable
policy design lives in the support skill's optional
[telemetry catalogue](Qyl.Telemetry.Tests/skills/qyl-telemetry-support/references/telemetry-policies.md).

[Build infrastructure](docs/build-infrastructure/README.md) describes MSBuild,
central packages and verification mechanics. [Implemented extension APIs](docs/extension-api.md)
distinguishes the receiver wrapper, architecture composition and policy proposals.
