---
name: qyl-telemetry-end-to-end
description: Write or review complete application-to-investigation scenarios in Qyl.Telemetry.EndToEndTests, including instrumented Qyl workloads, candidate delivery, NativeAOT artifacts and explicit REST or MCP paths.
---

# Application to investigation

Resolve package identities and execution assumptions through the
[execution context](../../../Qyl.Telemetry.Tests/skills/qyl-telemetry-support/references/execution-context.md) when relevant.
Target Qyl.Telemetry.EndToEndTests/Qyl.Telemetry.EndToEndTests.csproj.

Connect an observable application outcome to required telemetry and the actual
Collector investigation result. Keep workloads under Applications/ and scenario
expectations beside their delivery tests. Select the signals, producers and terminal
observations required by that scenario.

Determine the requested execution mode before choosing fixtures. For candidate
delivery, resolve the designated packages from the selected feed and manifest.
For NativeAOT delivery, execute the designated published native applications
and Collector artifacts. Record the package identities and artifact roles,
RIDs and hashes actually exercised.

Exercise REST and MCP through explicit transport paths. Reach the final
assertion through the promised investigation interface. Managed execution,
successful publication and backend calls establish their own narrower claims.

Require Collector readback for storage claims. Native span ownership requires
source, operation and parentage evidence; a span count alone is insufficient.

Share immutable publication under fixture ownership. Isolate mutable scenario
state and child-process workspaces. Bound readiness and terminal observation,
join children before cleanup and retain relevant failure evidence.

Use implemented shared policies that apply to the scenario. Move repeated
interpretation or execution into [shared support](../../../Qyl.Telemetry.Tests/skills/qyl-telemetry-support/SKILL.md).
