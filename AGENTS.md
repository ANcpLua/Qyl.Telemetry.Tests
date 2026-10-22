# Qyl.Telemetry.Tests

Test support for Qyl telemetry: Qyl.Telemetry.Tests (receiver, evidence, policies, Collector fixtures) and Qyl.Telemetry.Tests.Capture (NativeAOT capture, used by AutoInstrumentation as a submodule). Suites: UnitTests, IntegrationTests, EndToEndTests. Build with ./build.sh (Fallout Check); pins live in Version.props and global.json.

## Binding rules for every test

/Users/alexandernachtmann/RiderProjects/Qyl.Telemetry.Tests/SKILL.md: assertions by their exact semantics (AreEqual, HasCount, AreSequenceEqual with SequenceOrder.InAnyOrder, IsEmpty, ContainsSingle, ThrowsExactly), NativeAOT first with the native MTP runner, VSTest forbidden, AreEquivalent and Assert.That(Expression) off limits in NativeAOT code, nothing weakened. Verified against MSTest 4.5.1 on 2026-10-22.

/Users/alexandernachtmann/RiderProjects/Qyl.Telemetry.Tests/SKILL.md, section "Test-code rules": the name is the specification (`Subject_State_Expected`, no comments), one behavior per test with the MSTest assertion API, DataRow discriminators as constants, no try/catch/finally inside a test, time through TimeProvider, the gate decides. Read it before writing or reviewing a test.

## Skill per project

| Project | Skill |
| --- | --- |
| Qyl.Telemetry.Tests/Qyl.Telemetry.Tests.csproj | /Users/alexandernachtmann/RiderProjects/Qyl.Telemetry.Tests/Qyl.Telemetry.Tests/skills/qyl-telemetry-support/SKILL.md |
| Qyl.Telemetry.Tests.Capture/Qyl.Telemetry.Tests.Capture.csproj | /Users/alexandernachtmann/RiderProjects/Qyl.Telemetry.Tests/Qyl.Telemetry.Tests/skills/qyl-telemetry-support/SKILL.md (section "What lives where") |
| Qyl.Telemetry.UnitTests/Qyl.Telemetry.UnitTests.csproj | /Users/alexandernachtmann/RiderProjects/Qyl.Telemetry.Tests/Qyl.Telemetry.UnitTests/skills/qyl-telemetry-unit/SKILL.md |
| Qyl.Telemetry.IntegrationTests/Qyl.Telemetry.IntegrationTests.csproj | /Users/alexandernachtmann/RiderProjects/Qyl.Telemetry.Tests/Qyl.Telemetry.IntegrationTests/skills/qyl-telemetry-integration/SKILL.md |
| Qyl.Telemetry.EndToEndTests/Qyl.Telemetry.EndToEndTests.csproj | /Users/alexandernachtmann/RiderProjects/Qyl.Telemetry.Tests/Qyl.Telemetry.EndToEndTests/skills/qyl-telemetry-end-to-end/SKILL.md |

Support references: /Users/alexandernachtmann/RiderProjects/Qyl.Telemetry.Tests/Qyl.Telemetry.Tests/skills/qyl-telemetry-support/references/api.md (APIs and sources) and telemetry-policies.md (the policy attributes).
