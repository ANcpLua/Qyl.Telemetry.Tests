# Architecture and scope reference

`Qyl.Telemetry.Tests` provides shared test support. `Qyl.Telemetry.UnitTests`, `Qyl.Telemetry.IntegrationTests`, and `Qyl.Telemetry.EndToEndTests` cover the three test scopes described in [src/AGENTS.md](../src/AGENTS.md). New tests use MSTest 4.5.1. Package versions belong in `Directory.Packages.props`, not individual project files.

The repository build and test entry point is `Qyl.Telemetry.Tests.slnx`, containing exactly those four primary projects. Run tests through `pwsh ./eng/test.ps1`; the default unit selection uses the component-specific `Fast` category. Full build, coverage and inspection run through `./build.sh`.

Shared product and runtime pins are imported from the parent qyl `Version.props`; this repository keeps only its test-specific pins. Contracts, CLI and orchestration use project references. SemanticConventions also uses the local source checkout when present, with the shared package pin as the fallback. `Qyl.Telemetry.Hosting` remains a package dependency because its source is not in this checkout.

Rider's ReSharper Build can remain enabled. Real IDE builds explicitly build local project references before resolving their assemblies, preserving the selected Debug or Release configuration and building their transitive dependencies. Design-time builds do not run this step. The solution still contains only the four primary projects.

Projects with `UsesQylCollector=true` build the parent Collector and stage its output under `qyl/collector` beside the tests. They do not download a published Collector package, so local contract and Collector changes are exercised together on the next build.

## Test scopes, native composition and build metadata

Inherit the [repository guidance](../src/../AGENTS.md). This directory is the scope index; the existing compiled projects remain at their current paths. Choose the observable boundary before selecting a fixture. A project is a compiled code boundary; a skill describes a task or review goal. Do not invent competing MSTest skills just because several test projects exist.

| Scope | Project and governing skill |
| --- | --- |
| Unit | [Qyl.Telemetry.UnitTests](../src/../Qyl.Telemetry.UnitTests/AGENTS.md), [unit skill](../src/../Qyl.Telemetry.UnitTests/skills/qyl-telemetry-unit/SKILL.md) |
| Integration | [Qyl.Telemetry.IntegrationTests](../src/../Qyl.Telemetry.IntegrationTests/AGENTS.md), [integration skill](../src/../Qyl.Telemetry.IntegrationTests/skills/qyl-telemetry-integration/SKILL.md) |
| End-to-end | [Qyl.Telemetry.EndToEndTests](../src/../Qyl.Telemetry.EndToEndTests/AGENTS.md), [end-to-end skill](../src/../Qyl.Telemetry.EndToEndTests/skills/qyl-telemetry-end-to-end/SKILL.md) |
| Shared test helpers | [Qyl.Telemetry.Tests](../src/../Qyl.Telemetry.Tests/AGENTS.md), [support skill](../src/../Qyl.Telemetry.Tests/skills/qyl-telemetry-support/SKILL.md); [NativeAOT capture](../src/../Qyl.Telemetry.Tests.Capture/AGENTS.md) |

All scopes share the [native runner and assertion semantics](../src/../SKILL.md). Build metadata belongs in the project files, `Directory.Packages.props`, `global.json`, and `Qyl.Telemetry.Tests.slnx`; do not replace project references with copied binaries.
