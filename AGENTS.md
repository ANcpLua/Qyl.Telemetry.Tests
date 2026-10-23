# Qyl.Telemetry.Tests

The four primary projects are `Qyl.Telemetry.Tests` (shared support), `Qyl.Telemetry.UnitTests`, `Qyl.Telemetry.IntegrationTests`, and `Qyl.Telemetry.EndToEndTests`. `Qyl.Telemetry.Tests.slnx` contains exactly those four entries. Capture and application/probe projects remain build dependencies; do not add them as extra primary solution entries. Contract parity and Collector consumer cases belong to IntegrationTests/Contracts. Build with `./build.sh` (Fallout Check); pins live in `Version.props` and `global.json`. The build defaults to two logical processors and one MSBuild worker, preserving an explicitly supplied processor limit.

Keep exactly three `testconfig.json` files, beside the UnitTests, IntegrationTests and EndToEndTests `.csproj` files; pin `$schema` to the MSTest version in `global.json`.
Configure terminal output through `commandLineOptions`: UnitTests uses `failed` results, `Failed` stdout/stderr and `off` progress/ANSI; IntegrationTests/EndToEndTests use `all`, `All` and `auto`; set `mstest.output.captureTrace` to `Result` for UnitTests and `Live` for IntegrationTests/EndToEndTests.

## General test defaults

Unless a component-specific test policy applies:
- Name tests `<Method>_<Scenario>_<Expected>`.
- Mark unit tests with `[TestCategory("Unit")]`.
- Seal concrete test classes.

Read the nearest `AGENTS.md` and project skill before editing. A component-specific policy overrides these defaults only within that component. Keep MSTest at the version pinned by `global.json` (currently 4.5.1); package versions belong in `Directory.Packages.props`, not individual project files. Preserve the native Microsoft.Testing.Platform runner and the NativeAOT requirements in `SKILL.md`.

## Repository test execution

The source checkout must sit inside the parent qyl checkout: contract integration tests reference its Collector and contracts projects. CI recreates that layout and restores the MCP Bun dependencies; set the repository variable `QYL_SOURCE_REF` to pin the matching parent commit.

Run the unit suite without restoring with `pwsh ./eng/test.ps1 -Suite Unit -NoRestore`.
The script is the supported test entry point; do not substitute a direct `dotnet test` command. `Qyl.Telemetry.Tests.slnx` defines the repository's build and test graph. The `Unit` suite selects `TestCategory=Fast`, as required by its component policy, uses the native MTP runner, emits TRX results, and preserves the test exit code. Restore dependencies first when using `-NoRestore`. Other selections are `-Suite Integration`, `-Suite EndToEnd`, and `-Suite All`; the build gate calls the same script. Respect the caller's processor limit; the script defaults to two processors and one test module at a time.

## Inspection and completion

Run InspectCode against the **entire `Qyl.Telemetry.Tests.slnx`** before making changes and again after the requested work. Bash is supported. On the development Mac, run:

```bash
PATH="/usr/local/share/dotnet:$PATH" jb inspectcode Qyl.Telemetry.Tests.slnx --output=inspectcode.sarif
```

Use the installed .NET host and equivalent paths on a remote worker. The repository's `InspectCode` gate uses this same canonical solution; `eng/inspection.slnx` mirrors its four primary projects. Every agent working in this repository follows this rule, regardless of its starting project. Inspect and fix findings across all four primary project areas (shared support, unit, integration and end-to-end), and any other project registered in the solution; do not restrict repairs to the project that originally prompted the task.

Fix findings at their cause without asking for confirmation for routine in-scope repairs. When a production-code fix requires a test adjustment, make both changes and run the regression test that proves the production behavior. Do not weaken assertions, hide diagnostics, skip tests, or claim completion from compilation alone. Finish only after the relevant tests prove the fix and the final inspection succeeds; explain a concrete external blocker if that cannot be achieved. Keep intermediate messages to progress and blockers, and save the final report for completion.

## Binding rules for every test

[SKILL.md](SKILL.md): assertions by their exact semantics (AreEqual, HasCount, AreSequenceEqual with SequenceOrder.InAnyOrder, IsEmpty, ContainsSingle, ThrowsExactly), NativeAOT first with the native MTP runner, VSTest forbidden, AreEquivalent and Assert.That(Expression) off limits in NativeAOT code, nothing weakened. Verified against MSTest 4.5.1 on 2026-10-22; version-specific guidance rechecked on 2026-10-23.

[SKILL.md](SKILL.md), section "Test-code rules": the name is the specification (`<Method>_<Scenario>_<Expected>`, unless a component-specific policy overrides it, no comments), one behavior per test with the MSTest assertion API, DataRow discriminators as constants, no try/catch/finally inside a test, time through TimeProvider, the gate decides. Read it before writing or reviewing a test.

## Skill per project

| Project | Skill |
| --- | --- |
| Qyl.Telemetry.Tests/Qyl.Telemetry.Tests.csproj | [Support skill](Qyl.Telemetry.Tests/skills/qyl-telemetry-support/SKILL.md) |
| Qyl.Telemetry.UnitTests/Qyl.Telemetry.UnitTests.csproj | [Unit skill](Qyl.Telemetry.UnitTests/skills/qyl-telemetry-unit/SKILL.md) |
| Qyl.Telemetry.IntegrationTests/Qyl.Telemetry.IntegrationTests.csproj | [Integration skill](Qyl.Telemetry.IntegrationTests/skills/qyl-telemetry-integration/SKILL.md) |
| Qyl.Telemetry.EndToEndTests/Qyl.Telemetry.EndToEndTests.csproj | [End-to-end skill](Qyl.Telemetry.EndToEndTests/skills/qyl-telemetry-end-to-end/SKILL.md) |

Support references: [api.md](Qyl.Telemetry.Tests/skills/qyl-telemetry-support/references/api.md) (APIs and sources) and [telemetry-policies.md](Qyl.Telemetry.Tests/skills/qyl-telemetry-support/references/telemetry-policies.md) (the policy attributes).
