# Qyl telemetry tests

Test support and suites for Qyl telemetry.

| Project | Contents |
| --- | --- |
| `Qyl.Telemetry.Tests` | Support library: OTLP receiver, typed evidence, assertions, telemetry policies, Collector and application fixtures. |
| `Qyl.Telemetry.Tests.Capture` | NativeAOT Activity snapshots, CLR-array assertions and JSON reports, without MSTest or Collector dependencies. |
| `Qyl.Telemetry.UnitTests` | Deterministic decoding, registry validation, evidence, assertion and policy tests. |
| `Qyl.Telemetry.IntegrationTests` | Real OTLP transports, `AddQyl()` registration, receiver, Collector lifecycle and storage. |
| `Qyl.Telemetry.EndToEndTests` | Instrumented application to Collector, REST and MCP; published-package and NativeAOT delivery. |

## Run

Requires .NET SDK 10.0.402 and Node.js 24.

```sh
./build.sh                    # Check: build, three suites with coverage, InspectCode
./build.sh --target Test      # or Coverage, InspectCode
dotnet test --project Qyl.Telemetry.UnitTests/Qyl.Telemetry.UnitTests.csproj -c Release
```

Results: `artifacts/results` (TRX), `artifacts/coverage` (merged, informational),
`artifacts/inspection.sarif` (any finding fails), `artifacts/delivery` (delivery
artifacts and logs). Versions: `Version.props`, `global.json`.
The first delivery run needs network access and a native compiler.
