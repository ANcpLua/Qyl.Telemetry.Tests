# Qyl telemetry tests

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

The normal entrypoint executes the three suites through Microsoft.Testing.Platform
**2.5.1**, using **MSTest.Sdk 4.5.1**, net10.0 and the AllMicrosoft extension
profile (including Microsoft.CodeCoverage). The shared project is a library via
`IsTestApplication=false`. Package versions are centralized in
`Directory.Packages.props`; `NuGet.Config` selects the public feed explicitly.
One `QylPlatformVersion` property selects both the Collector runtime and
Orchestration package. TRX reports are written to `artifacts/results`.
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

The initial port passes **278 cases** through the solution entrypoint. It includes
real exporter/receiver transport and application-to-Collector persistence.
The retained Collector fixture currently launches the packaged **managed**
`qyl.collector.dll`; investigation assertions use the Collector's REST API.
This is managed execution against pinned public packages.

Candidate-package restore, execution of a published Native AOT Collector binary,
and MCP protocol investigation remain subsequent delivery work. MTP execution,
AOT publishing and MCP invocation are separate evidence boundaries. See
[the ownership map](docs/port.md) for retained and deferred source responsibilities.
