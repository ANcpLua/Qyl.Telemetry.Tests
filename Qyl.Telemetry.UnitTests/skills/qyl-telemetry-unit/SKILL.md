---
name: qyl-telemetry-unit
description: Write or review deterministic interpretation and policy tests in Qyl.Telemetry.UnitTests, including decoding, validation, correlation and assertion diagnostics.
---

# Deterministic interpretation and policy

Resolve package APIs or production policy through the
[execution context](../../../Qyl.Telemetry.Tests/skills/qyl-telemetry-support/references/execution-context.md) when relevant.
Target Qyl.Telemetry.UnitTests/Qyl.Telemetry.UnitTests.csproj.

Use explicit inputs and independently derived expected outcomes for protocol
decoding, captured-value transformations, exact numbers and timestamps,
registry-driven validation, trace relationships, metric correlation and the support library's assertions and diagnostics.

Distinguish conformance, violations and insufficient evidence where the rule
defines those outcomes. Assert type, field presence, identity and ordering when
they affect the contract.

Keep deterministic collaborators real. Replace uncontrolled effects at their
boundary without mocking the behavior under test. Use in-memory inputs and
controlled time; do not start receivers, applications or Collector processes,
open network connections or publish packages.

Keep each case focused on one behavior. Use native MSTest rows for independently
reportable inputs. Await asynchronous work and join it before disposing resources.
Use the focused project entrypoint for executable changes.

Prove each telemetry policy here with an exact violation and an accepted case.
When repeated expectations need shared interpretation or execution, use
[shared support](../../../Qyl.Telemetry.Tests/skills/qyl-telemetry-support/SKILL.md).
