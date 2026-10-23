# Test scopes, native composition and build metadata

Inherit the [repository guidance](../AGENTS.md). This directory is the scope index; the existing compiled projects remain at their current paths. Choose the observable boundary before selecting a fixture. A project is a compiled code boundary; a skill describes a task or review goal. Do not invent competing MSTest skills just because several test projects exist.

| Scope | Project and governing skill |
| --- | --- |
| Unit | [Qyl.Telemetry.UnitTests](../Qyl.Telemetry.UnitTests/AGENTS.md), [unit skill](../Qyl.Telemetry.UnitTests/skills/qyl-telemetry-unit/SKILL.md) |
| Integration | [Qyl.Telemetry.IntegrationTests](../Qyl.Telemetry.IntegrationTests/AGENTS.md), [integration skill](../Qyl.Telemetry.IntegrationTests/skills/qyl-telemetry-integration/SKILL.md) |
| End-to-end | [Qyl.Telemetry.EndToEndTests](../Qyl.Telemetry.EndToEndTests/AGENTS.md), [end-to-end skill](../Qyl.Telemetry.EndToEndTests/skills/qyl-telemetry-end-to-end/SKILL.md) |
| Shared test helpers | [Qyl.Telemetry.Tests](../Qyl.Telemetry.Tests/AGENTS.md), [support skill](../Qyl.Telemetry.Tests/skills/qyl-telemetry-support/SKILL.md); [NativeAOT capture](../Qyl.Telemetry.Tests.Capture/AGENTS.md) |

All scopes share the [native runner and assertion semantics](../SKILL.md). Build metadata belongs in the project files, `Directory.Packages.props`, `global.json`, and `Qyl.Telemetry.Tests.slnx`; do not replace project references with copied binaries.
