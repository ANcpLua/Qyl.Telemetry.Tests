# Contract integration tests

Inherit the [integration guidance](../AGENTS.md), [repository guidance](../../AGENTS.md), and [binding test skill](../../SKILL.md). These tests belong to Qyl.Telemetry.IntegrationTests; they are not another main test project.

Verify the current parent qyl C# contracts and TypeScript implementation using the same fixture inputs. Consumer tests may exercise internal Collector APIs through the explicit friend-assembly access. Preserve missing-versus-null, numeric fidelity and timestamp precision assertions. Bun and the parent qyl checkout are required. Keep fixture output paths under Contracts/Fixtures.
